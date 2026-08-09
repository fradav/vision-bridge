namespace VisionBridge

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Processing

/// Core vision pipeline: image loading, validation/downscaling, and LLM calls.
/// The server talks to an OpenAI-compatible chat/completions endpoint that
/// accepts image_url content parts (vision models).
module Vision =

    /// Maximum image dimension sent to the vision endpoint (OpenAI guidance).
    let private maxDimension = 1568

    type Config = { Endpoint: string; Model: string; ApiKey: string }

    let private env (name: string) =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.defaultValue ""

    /// Resolves endpoint/model/apiKey: explicit tool arguments take priority, and
    /// OPENAI_BASE_URL / OPENAI_MODEL / OPENAI_API_KEY env vars are the fallback.
    let resolveConfig (endpoint: string) (model: string) (apiKey: string) : Config =
        { Endpoint = if String.IsNullOrWhiteSpace endpoint then env "OPENAI_BASE_URL" else endpoint
          Model = if String.IsNullOrWhiteSpace model then env "OPENAI_MODEL" else model
          ApiKey = if String.IsNullOrWhiteSpace apiKey then env "OPENAI_API_KEY" else apiKey }

    /// Loads image bytes from a local file path or an http(s) URL.
    let loadImageBytes (image: string) (ct: CancellationToken) : Task<byte[]> = task {
        if String.IsNullOrWhiteSpace image then
            failwith "image input is empty"

        let isDataUrl = image.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        let isUrl =
            image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

        if isDataUrl then
            // data:<mime>;base64,<payload> — used by chat clients sending inline images.
            let comma = image.IndexOf(',')
            if comma < 0 then
                failwith "invalid data URL (missing ',' separator)"
            return Convert.FromBase64String(image.Substring(comma + 1))
        elif isUrl then
            use client = new HttpClient()
            // Wikimedia and other hosts reject requests without a descriptive
            // User-Agent, so send one on outbound image fetches.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("vision-bridge/1.0 (+https://github.com/fradav/vision-bridge)")
            client.Timeout <- TimeSpan.FromMinutes 2.
            use! resp = client.GetAsync(image, ct)
            resp.EnsureSuccessStatusCode() |> ignore
            return! resp.Content.ReadAsByteArrayAsync()
        else
            let fullPath = Path.GetFullPath image
            if not (File.Exists fullPath) then
                failwithf "image file not found: %s" fullPath
            return File.ReadAllBytes fullPath
    }

    /// Decodes, optionally downscales, and re-encodes the image as PNG bytes.
    /// Throws if the input is not a decodable image.
    let prepareImageBytes (raw: byte[]) : byte[] =
        use image = Image.Load raw
        let scale =
            if image.Width > maxDimension || image.Height > maxDimension then
                min (float maxDimension / float image.Width) (float maxDimension / float image.Height)
            else 1.0

        if scale < 1.0 then
            let newW = max 1 (int (float image.Width * scale))
            let newH = max 1 (int (float image.Height * scale))
            image.Mutate(fun ctx -> ctx.Resize(newW, newH) |> ignore)

        use ms = new MemoryStream()
        image.SaveAsPng ms
        ms.ToArray()

    let private dataUrl (bytes: byte[]) =
        "data:image/png;base64," + Convert.ToBase64String bytes

    /// Builds a chat request carrying the prompt plus one image_url part per image.
    let private buildPayload (model: string) (prompt: string) (imageDataUrls: string list) : string =
        let root = JsonObject()
        root["model"] <- JsonValue.Create model

        let messages = JsonArray()
        let userMsg = JsonObject()
        userMsg["role"] <- JsonValue.Create "user"

        let content = JsonArray()
        let textPart = JsonObject()
        textPart["type"] <- JsonValue.Create "text"
        textPart["text"] <- JsonValue.Create prompt
        content.Add textPart |> ignore

        for url in imageDataUrls do
            let imgPart = JsonObject()
            imgPart["type"] <- JsonValue.Create "image_url"
            let inner = JsonObject()
            inner["url"] <- JsonValue.Create url
            imgPart["image_url"] <- inner
            content.Add imgPart |> ignore

        userMsg["content"] <- content
        messages.Add userMsg |> ignore

        root["messages"] <- messages
        root["max_tokens"] <- JsonValue.Create 2048
        root.ToJsonString()

    /// Loads and prepares every image, returning one data URL per image in order.
    /// Loading is parallel (bounded concurrency) so several pages don't serialize.
    let private prepareDataUrls (images: string[]) (ct: CancellationToken) : Task<string list> = task {
        if images.Length = 0 then
            failwith "no images provided"
        if images |> Array.exists (fun i -> String.IsNullOrWhiteSpace i) then
            failwith "image input is empty"
        use sem = new SemaphoreSlim(4)
        let prepareOne (image: string) = task {
            let! _ = sem.WaitAsync(ct)
            try
                let! raw = loadImageBytes image ct
                let prepared = prepareImageBytes raw
                return dataUrl prepared
            finally
                sem.Release() |> ignore
        }
        let! urls = Task.WhenAll(images |> Array.map prepareOne)
        return urls |> Array.toList
    }

    /// Sends a vision chat request and returns the assistant text reply.
    let sendChatCompletion (config: Config) (prompt: string) (imageDataUrls: string list) (ct: CancellationToken) : Task<string> = task {
        if String.IsNullOrWhiteSpace config.Endpoint then
            failwith "No OpenAI-compatible endpoint configured. Set OPENAI_BASE_URL or pass the 'endpoint' argument."
        if String.IsNullOrWhiteSpace config.Model then
            failwith "No model configured. Set OPENAI_MODEL or pass the 'model' argument."

        let baseUrl = config.Endpoint.TrimEnd('/')
        let url = baseUrl + "/chat/completions"
        let payload = buildPayload config.Model prompt imageDataUrls

        use client = new HttpClient()
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vision-bridge/1.0 (+https://github.com/fradav/vision-bridge)")
        // Vision models can be slow (e.g. local llama-swap queues); the default 100s
        // HttpClient timeout would cancel long generations, so allow up to 10 minutes.
        client.Timeout <- TimeSpan.FromMinutes 10.
        if not (String.IsNullOrWhiteSpace config.ApiKey) then
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", config.ApiKey)
        use body = new StringContent(payload, Encoding.UTF8, "application/json")
        use! resp = client.PostAsync(url, body, ct)
        let! respText = resp.Content.ReadAsStringAsync(ct)

        if not resp.IsSuccessStatusCode then
            failwithf "LLM request failed (%d): %s" (int resp.StatusCode) respText

        let result =
            use doc = JsonDocument.Parse respText
            let contentElem =
                doc.RootElement.GetProperty("choices")
                |> fun choices -> choices.EnumerateArray() |> Seq.head
                |> fun c -> c.GetProperty("message").GetProperty("content")
            match contentElem.ValueKind with
            | JsonValueKind.String -> contentElem.GetString()
            | JsonValueKind.Array ->
                contentElem.EnumerateArray()
                |> Seq.map (fun p ->
                    if p.ValueKind = JsonValueKind.String then p.GetString()
                    else p.GetProperty("text").GetString())
                |> String.concat ""
            | _ -> failwith "Unexpected 'content' type in LLM response"

        return result
    }

    /// Default instruction used by analyze_image when no custom prompt is given.
    let private defaultAnalyzePrompt =
        "Describe the image in detail. Be precise about the visual content: subjects, objects, people, text, colors, and composition. Respond in plain text."

    /// Default instruction for multi-image analyze_image: describe every image,
    /// label it in order, and allow comparisons across the images of one request.
    let private multiImageAnalyzePrompt =
        "Describe each image in the order it was provided, labeling your descriptions as \"Image 1:\", \"Image 2:\", and so on. Be precise about each image's visual content: subjects, objects, people, text, colors, and composition. If the images are meant to be compared, compare them explicitly by these labels. Respond in plain text."

    /// Analyzes one or more images and returns a detailed textual description.
    /// When `prompt` is non-empty it replaces the default analyze prompt
    /// (guided/steered analysis).
    let analyzeImage (images: string[]) (endpoint: string) (model: string) (apiKey: string) (prompt: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! urls = prepareDataUrls images ct
        let prompt =
            if String.IsNullOrWhiteSpace prompt then
                if images.Length > 1 then multiImageAnalyzePrompt else defaultAnalyzePrompt
            else prompt
        let! text = sendChatCompletion config prompt urls ct
        return text
    }

    /// Extracts all text from one or more images using OCR. Each image's text is
    /// extracted and labeled in order ("Image 1:", "Image 2:", ...).
    let ocrImage (images: string[]) (endpoint: string) (model: string) (apiKey: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! urls = prepareDataUrls images ct
        let prompt =
            if images.Length > 1 then
                "Extract all text visible in each image using optical character recognition (OCR). Process the images in the order they were provided and label the extracted text as \"Image 1:\", \"Image 2:\", and so on. Return only the extracted text, preserving reading order as faithfully as possible. Do not add commentary."
            else
                "Extract all text visible in the image using optical character recognition (OCR). Return only the extracted text, preserving reading order as faithfully as possible. Do not add commentary."
        let! text = sendChatCompletion config prompt urls ct
        return text
    }
