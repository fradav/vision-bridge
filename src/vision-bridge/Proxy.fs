namespace VisionBridge

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// OpenAI-compatible proxy that makes a text-only LLM "vision-aware".
///
/// The proxy accepts standard chat/completions requests. Every `image_url`
/// content part (http(s) URL or data URL) is described by a separate VLM
/// upstream and rewritten in place into an indexed text part
/// `[Image N: <description>]`, so the LLM upstream only ever sees text. An
/// arbitrary number of images per request is supported (comparison, scanning
/// several pages, ...): images are described in parallel and each keeps its
/// reading-order index across the whole request.
module Proxy =

    let private env (name: string) =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.defaultValue ""

    type ProxyConfig =
        { LlmEndpoint: string
          LlmModel: string
          LlmApiKey: string
          VlmEndpoint: string
          VlmModel: string
          VlmApiKey: string
          Port: int }

    /// Reads the proxy configuration from the environment:
    /// OPENAI_BASE_URL / OPENAI_MODEL / OPENAI_API_KEY = LLM upstream,
    /// VLM_BASE_URL / VLM_MODEL / VLM_API_KEY = vision upstream,
    /// PROXY_PORT = listening port (default 8787).
    let resolveConfig () : ProxyConfig =
        let port =
            match env "PROXY_PORT" with
            | "" -> 8787
            | v -> int v
        { LlmEndpoint = env "OPENAI_BASE_URL"
          LlmModel = env "OPENAI_MODEL"
          LlmApiKey = env "OPENAI_API_KEY"
          VlmEndpoint = env "VLM_BASE_URL"
          VlmModel = env "VLM_MODEL"
          VlmApiKey = env "VLM_API_KEY"
          Port = port }

    /// Guided prompt sent to the VLM so a text-only LLM can reason over the
    /// image and compare it with the other images of the same request.
    let private vlmPrompt =
        "You are the vision module of a text-only language model. Describe this image precisely and completely so the text-only model can answer questions about it and compare it with other images: subjects, objects, people, layout, visible text, colors, and composition. Respond in plain text with no preamble."

    /// Value of a JSON part's "type" field ("" when absent).
    let private partType (o: JsonObject) =
        match o["type"] with
        | null -> ""
        | :? JsonValue as v -> v.GetValue<string>()
        | _ -> ""

    /// Collects every image URL (http(s) or data URL) appearing in the chat
    /// request, in reading order across all messages. Pure, so it is unit-testable.
    let collectImageUrls (request: JsonObject) : string list =
        let urls = ResizeArray<string>()
        let rec visit (node: JsonNode) =
            match node with
            | :? JsonObject as o ->
                if partType o = "image_url" then
                    match o["image_url"] with
                    | :? JsonObject as inner ->
                        match inner["url"] with
                        | :? JsonValue as jv -> urls.Add(jv.GetValue<string>())
                        | _ -> ()
                    | :? JsonValue as v -> urls.Add(v.GetValue<string>())
                    | _ -> ()
                else
                    for kv in o do visit kv.Value
            | :? JsonArray as arr ->
                for el in arr do visit el
            | _ -> ()
        visit request
        Seq.toList urls

    /// Replaces each image_url content part with a text part
    /// `[Image N: <description>]` (N = 1-based index across the whole request),
    /// keeping every other part and message intact. Pure, so it is unit-testable.
    let rewriteWithImages (request: JsonObject) (descriptions: string list) : JsonObject =
        let descs = descriptions |> Array.ofList
        let mutable imageCount = 0
        let rec rewrite (node: JsonNode) =
            match node with
            | :? JsonObject as o ->
                if partType o = "image_url" then
                    imageCount <- imageCount + 1
                    let n = imageCount
                    let text =
                        if n <= descs.Length then descs.[n - 1]
                        else "[unavailable]"
                    o.Remove("image_url") |> ignore
                    o["type"] <- JsonValue.Create "text"
                    o["text"] <- JsonValue.Create (sprintf "[Image %d: %s]" n text)
                else
                    for kv in o do rewrite kv.Value
            | :? JsonArray as arr ->
                for el in arr do rewrite el
            | _ -> ()
        rewrite request
        request

    /// Pins the LLM model and stream flag on the payload forwarded upstream.
    let prepareForward (request: JsonObject) (cfg: ProxyConfig) (stream: bool) : JsonObject =
        request["model"] <- JsonValue.Create cfg.LlmModel
        request["stream"] <- JsonValue.Create stream
        request

    /// Describes every image with the VLM upstream, in parallel (bounded
    /// concurrency). A failed image becomes a "[unavailable: <reason>]" note so
    /// the LLM can still proceed.
    let private describeAll (cfg: ProxyConfig) (urls: string list) (ct: CancellationToken) : Task<string list> = task {
        use sem = new SemaphoreSlim(4)
        let describeOne (url: string) = task {
            let! _ = sem.WaitAsync(ct)
            try
                try
                    let! t = Vision.analyzeImage [| url |] cfg.VlmEndpoint cfg.VlmModel cfg.VlmApiKey vlmPrompt ct
                    return t
                with ex ->
                    return sprintf "[unavailable: %s]" ex.Message
            finally
                sem.Release() |> ignore
        }
        let! results = Task.WhenAll(urls |> List.map describeOne)
        return results |> Array.toList
    }

    let private readBody (req: HttpListenerRequest) (ct: CancellationToken) : Task<byte[]> = task {
        use ms = new MemoryStream()
        do! req.InputStream.CopyToAsync(ms, ct)
        return ms.ToArray()
    }

    let private writeJson (ctx: HttpListenerContext) (status: int) (body: string) = task {
        let bytes = Encoding.UTF8.GetBytes body
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json"
        ctx.Response.ContentLength64 <- int64 bytes.Length
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        ctx.Response.Close()
    }

    let private modelsJson (cfg: ProxyConfig) =
        let root = JsonObject()
        root["object"] <- JsonValue.Create "list"
        let m = JsonObject()
        m["id"] <- JsonValue.Create cfg.LlmModel
        m["object"] <- JsonValue.Create "model"
        m["created"] <- JsonValue.Create 0
        m["owned_by"] <- JsonValue.Create "vision-bridge"
        let arr = JsonArray()
        arr.Add m |> ignore
        root["data"] <- arr
        root.ToJsonString()

    /// POSTs the rewritten payload to the LLM upstream. The HttpClient is
    /// returned (not disposed) so streaming callers can read the response body.
    let private postChat (cfg: ProxyConfig) (payload: JsonObject) (ct: CancellationToken) : Task<HttpClient * HttpResponseMessage> = task {
        let client = new HttpClient()
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vision-bridge-proxy/1.0 (+https://github.com/fradav/vision-bridge)")
        client.Timeout <- TimeSpan.FromMinutes 10.
        if not (String.IsNullOrWhiteSpace cfg.LlmApiKey) then
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", cfg.LlmApiKey)
        let url = cfg.LlmEndpoint.TrimEnd('/') + "/chat/completions"
        use body = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        let! resp = client.PostAsync(url, body, ct)
        return (client, resp)
    }

    let private relayJson (ctx: HttpListenerContext) (resp: HttpResponseMessage) = task {
        let! bytes = resp.Content.ReadAsByteArrayAsync()
        let media =
            resp.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.map (fun h -> h.MediaType)
            |> Option.defaultValue "application/json"
        ctx.Response.StatusCode <- int resp.StatusCode
        ctx.Response.ContentType <- media
        ctx.Response.ContentLength64 <- int64 bytes.Length
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        ctx.Response.Close()
    }

    /// Relays an upstream text/event-stream to the client, appending [DONE].
    let private relaySseStream (ctx: HttpListenerContext) (resp: HttpResponseMessage) = task {
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "text/event-stream"
        use! input = resp.Content.ReadAsStreamAsync()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let! n = input.ReadAsync(buffer, 0, buffer.Length)
            if n = 0 then
                finished <- true
            else
                do! ctx.Response.OutputStream.WriteAsync(buffer, 0, n)
                do! ctx.Response.OutputStream.FlushAsync()
        let doneBytes = Encoding.UTF8.GetBytes "\ndata: [DONE]\n\n"
        do! ctx.Response.OutputStream.WriteAsync(doneBytes, 0, doneBytes.Length)
        ctx.Response.Close()
    }

    /// Upstream answered JSON to a streaming request: emit it as one SSE chunk.
    let private relaySseJson (ctx: HttpListenerContext) (resp: HttpResponseMessage) = task {
        let! body = resp.Content.ReadAsStringAsync()
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "text/event-stream"
        let bytes = Encoding.UTF8.GetBytes(sprintf "data: %s\n\n" body)
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        let doneBytes = Encoding.UTF8.GetBytes "data: [DONE]\n\n"
        do! ctx.Response.OutputStream.WriteAsync(doneBytes, 0, doneBytes.Length)
        ctx.Response.Close()
    }

    let private isEventStream (resp: HttpResponseMessage) =
        let ct = resp.Content.Headers.ContentType
        ct <> null && ct.MediaType <> null && ct.MediaType = "text/event-stream"

    /// Handles POST /v1/chat/completions: rewrite images -> VLM descriptions,
    /// forward to the LLM, relay the answer (streamed or buffered).
    let private handleChat (cfg: ProxyConfig) (ctx: HttpListenerContext) (ct: CancellationToken) = task {
        let! raw = readBody ctx.Request ct
        let request =
            try
                Some(JsonNode.Parse(Encoding.UTF8.GetString(raw)) :?> JsonObject)
            with _ -> None
        match request with
        | None ->
            do! writeJson ctx 400 """{"error":{"message":"invalid JSON body","type":"invalid_request_error"}}"""
        | Some req ->
            let stream =
                match req["stream"] with
                | null -> false
                | :? JsonValue as v ->
                    let ok, b = v.TryGetValue<bool>()
                    if ok then b else false
                | _ -> false
            let urls = collectImageUrls req
            let! descs = describeAll cfg urls ct
            let rewritten = rewriteWithImages req descs
            let payload = prepareForward rewritten cfg stream
            let! (client, resp) = postChat cfg payload ct
            if stream && resp.IsSuccessStatusCode then
                use client = client
                use resp = resp
                if isEventStream resp then
                    do! relaySseStream ctx resp
                else
                    do! relaySseJson ctx resp
            else
                use client = client
                use resp = resp
                do! relayJson ctx resp
    }

    let private handleContext (cfg: ProxyConfig) (ctx: HttpListenerContext) (ct: CancellationToken) = task {
        let path = ctx.Request.Url.AbsolutePath
        let method = ctx.Request.HttpMethod
        if method = "GET" && (path = "/health" || path = "/health/") then
            do! writeJson ctx 200 """{"status":"ok"}"""
        elif method = "GET" && (path = "/v1/models" || path = "/models") then
            do! writeJson ctx 200 (modelsJson cfg)
        elif method = "POST" && (path = "/v1/chat/completions" || path = "/chat/completions") then
            do! handleChat cfg ctx ct
        else
            do! writeJson ctx 404 """{"error":{"message":"not found","type":"not_found"}}"""
    }

    /// Starts the OpenAI-compatible proxy on 127.0.0.1:<port> and blocks.
    let runProxy () : int =
        let cfg = resolveConfig ()
        if String.IsNullOrWhiteSpace cfg.LlmEndpoint || String.IsNullOrWhiteSpace cfg.LlmModel then
            eprintfn "PROXY: OPENAI_BASE_URL and/or OPENAI_MODEL not set — cannot start the proxy (LLM upstream)."
            1
        elif String.IsNullOrWhiteSpace cfg.VlmEndpoint || String.IsNullOrWhiteSpace cfg.VlmModel then
            eprintfn "PROXY: VLM_BASE_URL and/or VLM_MODEL not set — cannot start the proxy (vision upstream)."
            1
        else
            printfn "PROXY: listening on http://127.0.0.1:%d" cfg.Port
            printfn "PROXY: LLM upstream %s model=%s" cfg.LlmEndpoint cfg.LlmModel
            printfn "PROXY: VLM upstream %s model=%s" cfg.VlmEndpoint cfg.VlmModel

            use listener = new HttpListener()
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" cfg.Port)
            listener.Start()
            use cts = new CancellationTokenSource()

            let serve = task {
                while not cts.IsCancellationRequested do
                    try
                        let! ctx = listener.GetContextAsync()
                        task {
                            try
                                do! handleContext cfg ctx cts.Token
                            with ex ->
                                eprintfn "PROXY: request error: %s" ex.Message
                        }
                        |> ignore
                    with _ -> ()
            }

            serve.GetAwaiter().GetResult()
            0
