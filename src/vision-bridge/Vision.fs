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

        let isUrl =
            image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

        if isUrl then
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

    let private buildPayload (model: string) (prompt: string) (imageDataUrl: string) : string =
        let root = JsonObject()
        root["model"] <- JsonValue.Create model

        let messages = JsonArray()
        let userMsg = JsonObject()
        userMsg["role"] <- JsonValue.Create "user"

        let content = JsonArray()
        let textPart = JsonObject()
        textPart["type"] <- JsonValue.Create "text"
        textPart["text"] <- JsonValue.Create prompt

        let imgPart = JsonObject()
        imgPart["type"] <- JsonValue.Create "image_url"
        let inner = JsonObject()
        inner["url"] <- JsonValue.Create imageDataUrl
        imgPart["image_url"] <- inner

        content.Add textPart |> ignore
        content.Add imgPart |> ignore
        userMsg["content"] <- content
        messages.Add userMsg |> ignore

        root["messages"] <- messages
        root["max_tokens"] <- JsonValue.Create 2048
        root.ToJsonString()

    /// Sends a vision chat request and returns the assistant text reply.
    let sendChatCompletion (config: Config) (prompt: string) (imageDataUrl: string) (ct: CancellationToken) : Task<string> = task {
        if String.IsNullOrWhiteSpace config.Endpoint then
            failwith "No OpenAI-compatible endpoint configured. Set OPENAI_BASE_URL or pass the 'endpoint' argument."
        if String.IsNullOrWhiteSpace config.Model then
            failwith "No model configured. Set OPENAI_MODEL or pass the 'model' argument."

        let baseUrl = config.Endpoint.TrimEnd('/')
        let url = baseUrl + "/chat/completions"
        let payload = buildPayload config.Model prompt imageDataUrl

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

    /// Analyzes an image and returns a detailed textual description. When `prompt`
    /// is non-empty it replaces the default analyze prompt (guided/steered analysis).
    let analyzeImage (image: string) (endpoint: string) (model: string) (apiKey: string) (prompt: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! raw = loadImageBytes image ct
        let prepared = prepareImageBytes raw
        let prompt = if String.IsNullOrWhiteSpace prompt then defaultAnalyzePrompt else prompt
        let! text = sendChatCompletion config prompt (dataUrl prepared) ct
        return text
    }

    /// Extracts all text from an image using OCR.
    let ocrImage (image: string) (endpoint: string) (model: string) (apiKey: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! raw = loadImageBytes image ct
        let prepared = prepareImageBytes raw
        let prompt =
            "Extract all text visible in the image using optical character recognition (OCR). Return only the extracted text, preserving reading order as faithfully as possible. Do not add commentary."
        let! text = sendChatCompletion config prompt (dataUrl prepared) ct
        return text
    }
