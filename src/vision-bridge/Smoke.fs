namespace VisionBridge

open System
open System.IO
open System.Net
open System.Net.Sockets
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

    /// Runs a vision function against `input` (a local path or a URL), labels the
    /// result, and returns Some(text) on success or None on failure.
    let private run
        (f: string -> string -> string -> string -> CancellationToken -> Task<string>)
        (label: string)
        (input: string)
        (cfg: Vision.Config)
        : string option =
        try
            let text = f input cfg.Endpoint cfg.Model cfg.ApiKey CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
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
    let runSmoke () : int =
        let cfg = Vision.resolveConfig "" "" ""

        if String.IsNullOrWhiteSpace cfg.Endpoint || String.IsNullOrWhiteSpace cfg.Model then
            eprintfn "SMOKE: OPENAI_BASE_URL and/or OPENAI_MODEL not set — cannot run the real-endpoint smoke test."
            1
        else
            let photo = Path.Combine(samplesDir, "photo.jpg")
            let sign = Path.Combine(samplesDir, "text-sign.jpg")

            printfn "SMOKE: endpoint=%s model=%s apiKey=%s" cfg.Endpoint cfg.Model (if String.IsNullOrWhiteSpace cfg.ApiKey then "(none)" else "set")

            use listener = new HttpListener()
            let port = freePort ()
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()
            let baseUrl = sprintf "http://127.0.0.1:%d" port

            use cts = new CancellationTokenSource()
            let serveTask =
                task {
                    while not cts.IsCancellationRequested do
                        try
                            let! ctx = listener.GetContextAsync()
                            let bytes =
                                match ctx.Request.Url.AbsolutePath with
                                | "/photo.jpg" -> Some(File.ReadAllBytes photo)
                                | "/text-sign.jpg" -> Some(File.ReadAllBytes sign)
                                | _ -> None
                            match bytes with
                            | Some b ->
                                ctx.Response.ContentType <- "image/jpeg"
                                ctx.Response.StatusCode <- 200
                                ctx.Response.OutputStream.Write(b, 0, b.Length)
                                ctx.Response.Close()
                            | None ->
                                ctx.Response.StatusCode <- 404
                                ctx.Response.Close()
                        with _ -> ()
                }

            try
                // analyze_image: local file + URL
                let analyzeFile = run Vision.analyzeImage "analyze_image(file)" photo cfg
                let analyzeUrl = run Vision.analyzeImage "analyze_image(url)" (baseUrl + "/photo.jpg") cfg

                // ocr_image: local file + URL
                let ocrFile = run Vision.ocrImage "ocr_image(file)" sign cfg
                let ocrUrl = run Vision.ocrImage "ocr_image(url)" (baseUrl + "/text-sign.jpg") cfg

                let ok =
                    (analyzeFile |> Option.exists nonEmpty)
                    && (analyzeUrl |> Option.exists nonEmpty)
                    && (ocrFile |> Option.exists hasText)
                    && (ocrUrl |> Option.exists hasText)

                if ok then
                    printfn "SMOKE: PASS"
                    0
                else
                    eprintfn "SMOKE: FAILED — one or more assertions did not hold."
                    1
            finally
                cts.Cancel()
                listener.Stop()
