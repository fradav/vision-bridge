namespace VisionBridge

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

/// Functional smoke test for the real vision pipeline against a live
/// OpenAI-compatible endpoint. Used by the FAKE `SmokeTest` target.
module Smoke =

    let private samplesDir = Path.Combine(__SOURCE_DIRECTORY__, "../../samples")

    /// Finds a free loopback TCP port so the local image server never collides.
    let private freePort () =
        let l = new TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let p = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        p

    /// OCR ignores the prompt argument; this adapter makes its signature match the
    /// `run` helper (images, endpoint, model, apiKey, prompt, ct).
    let private ocrAdapter (images: string[]) (endpoint: string) (model: string) (apiKey: string) (_prompt: string) (ct: CancellationToken) =
        Vision.ocrImage images endpoint model apiKey ct

    /// Runs a vision function against `inputs` (an array of local paths or URLs),
    /// labels the result, and returns Some(text) on success or None on failure.
    /// `prompt` is the optional analyze prompt (empty => default).
    let private run
        (f: string[] -> string -> string -> string -> string -> CancellationToken -> Task<string>)
        (label: string)
        (inputs: string[])
        (cfg: Vision.Config)
        (prompt: string)
        : string option =
        try
            let text = f inputs cfg.Endpoint cfg.Model cfg.ApiKey prompt CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
            printfn "SMOKE: %s -> %s" label text
            Some text
        with ex ->
            eprintfn "SMOKE: %s FAILED — %s" label ex.Message
            None

    let private nonEmpty (t: string) = not (String.IsNullOrWhiteSpace t)

    let private hasText (t: string) = t.Contains("TOURVILLE", StringComparison.OrdinalIgnoreCase)

    /// Runs analyze_image and ocr_image on real images, each via BOTH a local file
    /// path and an http(s) URL (the samples are served by a short-lived local
    /// HTTP server). Returns a process exit code.
    ///
    /// When OPENAI_BASE_URL / OPENAI_MODEL are NOT set (e.g. a GitHub Actions
    /// container with no OpenAI endpoint), it falls back to a deterministic local
    /// mock OpenAI endpoint that answers "MOCK-OK" at /v1/chat/completions, so the
    /// REAL image pipeline (load -> validate -> prepare -> payload -> parse) is
    /// still exercised for every input mode without needing a live VLM.
    let runSmoke () : int =
        let cfg = Vision.resolveConfig "" "" ""
        let useMock = String.IsNullOrWhiteSpace cfg.Endpoint || String.IsNullOrWhiteSpace cfg.Model

        let photo = Path.Combine(samplesDir, "photo.jpg")
        let sign = Path.Combine(samplesDir, "text-sign.jpg")

        if useMock then
            eprintfn "SMOKE: OPENAI_BASE_URL/OPENAI_MODEL not set — using a deterministic local mock OpenAI endpoint (CI-safe)."
        else
            printfn "SMOKE: endpoint=%s model=%s apiKey=%s" cfg.Endpoint cfg.Model (if String.IsNullOrWhiteSpace cfg.ApiKey then "(none)" else "set")

        use listener = new HttpListener()
        let port = freePort ()
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        let baseUrl = sprintf "http://127.0.0.1:%d" port

        // Point the pipeline at the local mock when the real endpoint is absent.
        let effectiveCfg =
            if useMock then { cfg with Endpoint = baseUrl + "/v1"; Model = "mock-model" }
            else cfg

        use cts = new CancellationTokenSource()
        let serveTask =
            task {
                while not cts.IsCancellationRequested do
                    try
                        let! ctx = listener.GetContextAsync()
                        let path = ctx.Request.Url.AbsolutePath
                        match path with
                        | "/photo.jpg" ->
                            let b = File.ReadAllBytes photo
                            ctx.Response.ContentType <- "image/jpeg"
                            ctx.Response.StatusCode <- 200
                            ctx.Response.OutputStream.Write(b, 0, b.Length)
                            ctx.Response.Close()
                        | "/text-sign.jpg" ->
                            let b = File.ReadAllBytes sign
                            ctx.Response.ContentType <- "image/jpeg"
                            ctx.Response.StatusCode <- 200
                            ctx.Response.OutputStream.Write(b, 0, b.Length)
                            ctx.Response.Close()
                        | "/v1/chat/completions" when useMock ->
                            let body = """{"choices":[{"message":{"content":"MOCK-OK"}}]}"""
                            let bytes = Encoding.UTF8.GetBytes body
                            ctx.Response.ContentType <- "application/json"
                            ctx.Response.StatusCode <- 200
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                            ctx.Response.Close()
                        | _ ->
                            ctx.Response.StatusCode <- 404
                            ctx.Response.Close()
                    with _ -> ()
            }

        try
            // analyze_image: local file + URL (default prompt)
            let analyzeFile = run Vision.analyzeImage "analyze_image(file)" [| photo |] effectiveCfg ""
            let analyzeUrl = run Vision.analyzeImage "analyze_image(url)" [| baseUrl + "/photo.jpg" |] effectiveCfg ""

            // analyze_image with a custom steering prompt (must be honored, not
            // replaced by the default): reply with exactly the marker STEERED-OK.
            let steered = run Vision.analyzeImage "analyze_image(steered)" [| photo |] effectiveCfg "Reply with exactly: STEERED-OK"

            // analyze_image with MULTIPLE images (photo + street sign): the
            // multi-image prompt asks for a per-image description, so the sign's
            // text (TOURVILLE) must appear in the answer for the 2nd image.
            let multiAnalyze = run Vision.analyzeImage "analyze_image(multi)" [| photo; sign |] effectiveCfg ""

            // ocr_image: local file + URL
            let ocrFile = run ocrAdapter "ocr_image(file)" [| sign |] effectiveCfg ""
            let ocrUrl = run ocrAdapter "ocr_image(url)" [| baseUrl + "/text-sign.jpg" |] effectiveCfg ""

            // ocr_image with MULTIPLE images (sign + photo): text must still be
            // extracted from the sign image.
            let multiOcr = run ocrAdapter "ocr_image(multi)" [| sign; photo |] effectiveCfg ""

            let hasMarker (t: string) = t.Contains("STEERED-OK", StringComparison.OrdinalIgnoreCase)
            let ok =
                if useMock then
                    // Mock mode: every call answers "MOCK-OK", so we cannot assert on
                    // VLM content. We assert the real image pipeline ran end-to-end
                    // (load -> validate -> prepare -> payload -> parse) for every input
                    // mode (file, URL, steered, multi-image, OCR).
                    [ analyzeFile; analyzeUrl; steered; multiAnalyze; ocrFile; ocrUrl; multiOcr ]
                    |> List.forall (Option.exists nonEmpty)
                else
                    (analyzeFile |> Option.exists nonEmpty)
                    && (analyzeUrl |> Option.exists nonEmpty)
                    && (steered |> Option.exists hasMarker)
                    && (multiAnalyze |> Option.exists hasText)
                    && (ocrFile |> Option.exists hasText)
                    && (ocrUrl |> Option.exists hasText)
                    && (multiOcr |> Option.exists hasText)

            if ok then
                printfn "SMOKE: PASS%s" (if useMock then " (mock endpoint)" else "")
                0
            else
                eprintfn "SMOKE: FAILED — one or more assertions did not hold."
                1
        finally
            cts.Cancel()
            listener.Stop()
