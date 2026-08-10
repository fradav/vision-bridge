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
open ImageMagick

/// Core vision pipeline: image loading, validation/downscaling, and LLM calls.
/// The server talks to an OpenAI-compatible chat/completions endpoint that
/// accepts image_url content parts (vision models).
module Vision =

    /// Maximum image dimension sent to the vision endpoint (OpenAI guidance).
    let private maxDimension = 1568

    /// Maximum number of image_url parts sent in a single chat request. Some
    /// vision backends cap images-per-message or truncate large payloads, which
    /// made multi-image calls flaky (the VLM saw only a subset of the images).
    /// Larger sets are split into at most this many per request, and each image is
    /// preceded by a text marker so llama.cpp does not frame-merge consecutive
    /// images (ggml-org/llama.cpp#24303). The labeled replies are then
    /// concatenated, so every request stays small enough that all of its images
    /// are processed.
    let private maxImagesPerRequest = 4

    /// JPEG quality used when a downscaled photo is re-encoded: keeps quality
    /// high while keeping the payload small for the vision endpoint.
    let private jpegQuality = 88u

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

    /// Sniffs the MIME type of an image from its magic bytes so pass-through
    /// data URLs keep the original codec (png/jpeg/gif/webp/bmp/tiff) instead of
    /// hardcoding PNG. Falls back to image/png for unknown inputs.
    let private sniffMime (raw: byte[]) : string =
        let n = raw.Length
        if n >= 8 && raw[0] = 0x89uy && raw[1] = 0x50uy && raw[2] = 0x4Euy && raw[3] = 0x47uy then "image/png"
        elif n >= 3 && raw[0] = 0xFFuy && raw[1] = 0xD8uy && raw[2] = 0xFFuy then "image/jpeg"
        elif n >= 4 && raw[0] = 0x47uy && raw[1] = 0x49uy && raw[2] = 0x46uy && raw[3] = 0x38uy then "image/gif"
        elif n >= 12 && raw[0] = 0x52uy && raw[1] = 0x49uy && raw[2] = 0x46uy && raw[3] = 0x46uy
              && raw[8] = 0x57uy && raw[9] = 0x45uy && raw[10] = 0x42uy && raw[11] = 0x50uy then "image/webp"
        elif n >= 4 && raw[0] = 0x42uy && raw[1] = 0x4Duy then "image/bmp"
        elif n >= 4 && (raw[0] = 0x49uy && raw[1] = 0x49uy) || (raw[0] = 0x4Duy && raw[1] = 0x4Duy) then "image/tiff"
        else "image/png"

    /// Decodes the image with Magick.NET (ImageMagick). If it already fits within
    /// the endpoint's dimension limit it is passed through UNCHANGED — the exact
    /// original bytes, so there is no re-encode artifact and no quality loss.
    /// Only when a dimension exceeds maxDimension is the image downscaled with a
    /// high-quality Lanczos filter and re-encoded: PNG stays lossless PNG,
    /// everything else becomes a high-quality JPEG. Throws if the input is not a
    /// decodable image. Returns the MIME type and the bytes to send.
    let prepareImageBytes (raw: byte[]) : string * byte[] =
        let mime = sniffMime raw
        use image = new MagickImage(raw)
        if int image.Width <= maxDimension && int image.Height <= maxDimension then
            (mime, raw)
        else
            let scale =
                min (float maxDimension / float image.Width) (float maxDimension / float image.Height)
            let newW = max 1u (uint32 (float image.Width * scale))
            let newH = max 1u (uint32 (float image.Height * scale))
            image.FilterType <- FilterType.Lanczos
            image.Resize(newW, newH)
            let outMime, fmt =
                if mime = "image/png" then "image/png", MagickFormat.Png
                else "image/jpeg", MagickFormat.Jpeg
            if fmt = MagickFormat.Jpeg then
                image.Quality <- jpegQuality
            use ms = new MemoryStream()
            image.Write(ms, fmt)
            (outMime, ms.ToArray())

    let private dataUrl (mime: string, bytes: byte[]) =
        "data:" + mime + ";base64," + Convert.ToBase64String bytes

    /// Builds a chat request carrying the prompt plus one image_url part per image.
    /// `startIndex` is the 1-based GLOBAL index of the first image in `imageDataUrls`
    /// (used when a large set is split into several chat requests).
    ///
    /// A text marker is interleaved BEFORE every image part. This is the fix for the
    /// llama.cpp "frame-merge" bug (ggml-org/llama.cpp#24303): consecutive image_url
    /// parts in one user message are merged into video frames, so the VLM sees only a
    /// subset of the images. Inserting a text part between images keeps each one
    /// independent and reliably processed. The markers also reinforce the global
    /// `Image N:` labels used by the prompt.
    let private buildPayload (model: string) (prompt: string) (startIndex: int) (imageDataUrls: string list) : string =
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

        let mutable n = 0
        for url in imageDataUrls do
            // Text marker before each image (and thus between consecutive images).
            let marker = JsonObject()
            marker["type"] <- JsonValue.Create "text"
            marker["text"] <- JsonValue.Create (sprintf "Image %d:" (startIndex + n))
            content.Add marker |> ignore

            let imgPart = JsonObject()
            imgPart["type"] <- JsonValue.Create "image_url"
            let inner = JsonObject()
            inner["url"] <- JsonValue.Create url
            imgPart["image_url"] <- inner
            content.Add imgPart |> ignore
            n <- n + 1

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
    /// `startIndex` is the 1-based GLOBAL index of the first image in `imageDataUrls`.
    let sendChatCompletion (config: Config) (prompt: string) (startIndex: int) (imageDataUrls: string list) (ct: CancellationToken) : Task<string> = task {
        if String.IsNullOrWhiteSpace config.Endpoint then
            failwith "No OpenAI-compatible endpoint configured. Set OPENAI_BASE_URL or pass the 'endpoint' argument."
        if String.IsNullOrWhiteSpace config.Model then
            failwith "No model configured. Set OPENAI_MODEL or pass the 'model' argument."

        let baseUrl = config.Endpoint.TrimEnd('/')
        let url = baseUrl + "/chat/completions"
        let payload = buildPayload config.Model prompt startIndex imageDataUrls

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

    /// Labels for a contiguous run of images starting at `start` (1-based).
    let private labelList (start: int) (count: int) =
        [ for i in start .. start + count - 1 -> sprintf "Image %d:" i ]
        |> String.concat ", "

    /// Builds the analyze prompt for images [start .. start+count-1] of a set of
    /// `total`. A single image gets the default prompt; otherwise every image is
    /// labeled with its GLOBAL marker so batched replies keep consistent labels.
    let private buildAnalyzePrompt (total: int) (start: int) (count: int) =
        if count = 1 && total = 1 then
            defaultAnalyzePrompt
        else
            sprintf "Describe each image in the order it was provided, labeling your descriptions as %s. Be precise about each image's visual content: subjects, objects, people, text, colors, and composition. If the images are meant to be compared, compare them explicitly by these labels. Respond in plain text." (labelList start count)

    /// Builds the OCR prompt for images [start .. start+count-1] of a set of `total`.
    let private buildOcrPrompt (total: int) (start: int) (count: int) =
        if count = 1 && total = 1 then
            "Extract all text visible in the image using optical character recognition (OCR). Return only the extracted text, preserving reading order as faithfully as possible. Do not add commentary."
        else
            sprintf "Extract all text visible in each image using optical character recognition (OCR). Process the images in the order they were provided and label the extracted text as %s. Return only the extracted text, preserving reading order as faithfully as possible. Do not add commentary." (labelList start count)

    /// Adapts a user-supplied analyze prompt for a batched subset of images: when
    /// the whole set is sent in one request the prompt is untouched; when images
    /// are split into several requests, a note pins the global markers for this
    /// batch so per-image descriptions stay consistent across the whole call.
    let private adaptCustomPrompt (userPrompt: string) (total: int) (start: int) (count: int) =
        if total = count then userPrompt
        else
            let labels = labelList start count
            sprintf "%s\n\nThis request shows images %d through %d of %d (%s). Label any per-image descriptions with the corresponding Image N: marker." userPrompt start (start + count - 1) total labels

    /// Sends the chat request(s) for every image. When there are more than
    /// maxImagesPerRequest images they are split into chunks and sent as separate
    /// requests (bounded concurrency), then the labeled replies are concatenated
    /// in order — this keeps each request small enough that the VLM processes all
    /// of its images (fixing the flaky multi-image behavior). `promptFor` receives
    /// (total, start, count) and returns the prompt for that batch.
    let private sendBatched (config: Config) (promptFor: int -> int -> int -> string) (urls: string list) (ct: CancellationToken) : Task<string> = task {
        let total = urls.Length
        if total <= maxImagesPerRequest then
            return! sendChatCompletion config (promptFor total 1 total) 1 urls ct
        else
            let chunks =
                [ for start in 1 .. maxImagesPerRequest .. total ->
                    let count = min maxImagesPerRequest (total - start + 1)
                    (start, urls |> List.skip (start - 1) |> List.take count) ]
            use sem = new SemaphoreSlim(4)
            let sendOne (start: int, chunk: string list) = task {
                let! _ = sem.WaitAsync(ct)
                try
                    let prompt = promptFor total start chunk.Length
                    return! sendChatCompletion config prompt start chunk ct
                finally
                    sem.Release() |> ignore
            }
            let! results = Task.WhenAll(chunks |> List.map sendOne)
            return results |> String.concat "\n\n"
    }

    /// Analyzes one or more images and returns a detailed textual description.
    /// When `prompt` is non-empty it replaces the default analyze prompt
    /// (guided/steered analysis).
    let analyzeImage (images: string[]) (endpoint: string) (model: string) (apiKey: string) (prompt: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! urls = prepareDataUrls images ct
        let promptFor total start count =
            if String.IsNullOrWhiteSpace prompt then buildAnalyzePrompt total start count
            else adaptCustomPrompt prompt total start count
        let! text = sendBatched config promptFor urls ct
        return text
    }

    /// Extracts all text from one or more images using OCR. Each image's text is
    /// extracted and labeled in order ("Image 1:", "Image 2:", ...).
    let ocrImage (images: string[]) (endpoint: string) (model: string) (apiKey: string) (ct: CancellationToken) : Task<string> = task {
        let config = resolveConfig endpoint model apiKey
        let! urls = prepareDataUrls images ct
        let promptFor total start count = buildOcrPrompt total start count
        let! text = sendBatched config promptFor urls ct
        return text
    }
