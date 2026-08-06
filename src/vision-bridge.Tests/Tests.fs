module Tests

open System
open System.IO
open System.Net
open System.Text
open System.Threading
open Expecto
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open VisionBridge

/// Generates a PNG of the given dimensions.
let makePng (w: int) (h: int) : byte[] =
    use image = new Image<Rgba32>(w, h)
    use ms = new MemoryStream()
    image.SaveAsPng ms
    ms.ToArray()

[<Tests>]
let imageTests =
    testList "Image" [
        test "prepareImageBytes returns a PNG" {
            let png = makePng 64 64
            let prepared = Vision.prepareImageBytes png
            Expect.equal prepared[0] 0x89uy "PNG signature byte 0"
            Expect.equal prepared[1] 0x50uy "PNG signature byte 1"
        }

        test "prepareImageBytes downscales large images" {
            let png = makePng 4000 4000
            let prepared = Vision.prepareImageBytes png
            use img = Image.Load prepared
            Expect.isTrue (img.Width <= 1568) "width within limit"
            Expect.isTrue (img.Height <= 1568) "height within limit"
        }

        test "loadImageBytes fails on a missing file" {
            let ran =
                try
                    Vision.loadImageBytes "/definitely/not/a/real/file.png" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> ignore
                    false
                with _ -> true
            Expect.isTrue ran "missing file should raise"
        }

        test "resolveConfig falls back to environment variables" {
            // set then unset to avoid leaking state
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "http://localhost:8080/v1")
            Environment.SetEnvironmentVariable("OPENAI_MODEL", "qwen3.6-moe:instruct")
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-123")
            let cfg = Vision.resolveConfig "" "" ""
            Expect.equal cfg.Endpoint "http://localhost:8080/v1" "endpoint from env"
            Expect.equal cfg.Model "qwen3.6-moe:instruct" "model from env"
            Expect.equal cfg.ApiKey "sk-test-123" "apiKey from env"

            let cfg2 = Vision.resolveConfig "http://override/v1" "override-model" "sk-override"
            Expect.equal cfg2.Endpoint "http://override/v1" "endpoint override wins"
            Expect.equal cfg2.Model "override-model" "model override wins"
            Expect.equal cfg2.ApiKey "sk-override" "apiKey override wins"

            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", null)
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null)
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
        }
    ]

[<Tests>]
let integrationTests =
    testList "Integration" [
        testCase "analyze_image and ocr_image end-to-end via a local mock endpoint" (fun () ->
            use cts = new CancellationTokenSource()
            use listener = new HttpListener()
            let port = 8123
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()

            let imageBytes = makePng 64 64
            let baseUrl = sprintf "http://127.0.0.1:%d" port

            let serveTask =
                task {
                    while not cts.IsCancellationRequested do
                        try
                            let! ctx = listener.GetContextAsync()
                            let path = ctx.Request.Url.AbsolutePath
                            if path = "/image.png" then
                                ctx.Response.ContentType <- "image/png"
                                ctx.Response.StatusCode <- 200
                                ctx.Response.OutputStream.Write(imageBytes, 0, imageBytes.Length)
                                ctx.Response.Close()
                            elif path = "/chat/completions" then
                                let body = """{"choices":[{"message":{"content":"MOCK-OK"}}]}"""
                                let bytes = Encoding.UTF8.GetBytes body
                                ctx.Response.ContentType <- "application/json"
                                ctx.Response.StatusCode <- 200
                                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                                ctx.Response.Close()
                            else
                                ctx.Response.StatusCode <- 404
                                ctx.Response.Close()
                        with _ -> ()
                }

            try
                // Local file -> analyze_image (default prompt)
                let tmp = Path.Combine(Path.GetTempPath(), "vb-test.png")
                File.WriteAllBytes(tmp, imageBytes)
                let r1 =
                    Vision.analyzeImage tmp baseUrl "mock-model" "" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r1 "MOCK-OK" "analyze_image on a local file"

                // Local file -> analyze_image with a custom steering prompt
                let r1b =
                    Vision.analyzeImage tmp baseUrl "mock-model" "" "Reply with exactly: STEERED-OK" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r1b "MOCK-OK" "analyze_image with a custom prompt"

                // URL -> ocr_image
                let r2 =
                    Vision.ocrImage (baseUrl + "/image.png") baseUrl "mock-model" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r2 "MOCK-OK" "ocr_image from a URL"
            finally
                cts.Cancel()
                listener.Stop()
        )
    ]
