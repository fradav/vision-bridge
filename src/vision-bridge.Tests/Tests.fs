module Tests

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Expecto
open ImageMagick
open VisionBridge
open VisionBridge.Proxy

/// Generates a PNG of the given dimensions (via Magick.NET).
let makePng (w: int) (h: int) : byte[] =
    use image = new MagickImage(MagickColors.Red, uint32 w, uint32 h)
    use ms = new MemoryStream()
    image.Write(ms, MagickFormat.Png)
    ms.ToArray()

[<Tests>]
let imageTests =
    testList "Image" [
        test "prepareImageBytes returns a PNG" {
            let png = makePng 64 64
            let (_, prepared) = Vision.prepareImageBytes png
            Expect.equal prepared[0] 0x89uy "PNG signature byte 0"
            Expect.equal prepared[1] 0x50uy "PNG signature byte 1"
        }

        test "prepareImageBytes passes through images that already fit" {
            // Images within the dimension limit must be returned unchanged (exact
            // original bytes) so there is no re-encode artifact or quality loss.
            let png = makePng 64 64
            let (mime, prepared) = Vision.prepareImageBytes png
            Expect.equal mime "image/png" "MIME sniffed from PNG signature"
            Expect.equal prepared png "fits -> original bytes passed through"
        }

        test "prepareImageBytes downscales large images" {
            let png = makePng 4000 4000
            let (_, prepared) = Vision.prepareImageBytes png
            use img = new MagickImage(prepared)
            Expect.isTrue (int img.Width <= 1568) "width within limit"
            Expect.isTrue (int img.Height <= 1568) "height within limit"
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

        test "loadImageBytes decodes a data URL" {
            let png = makePng 32 32
            let dataUrl = "data:image/png;base64," + Convert.ToBase64String png
            let bytes =
                Vision.loadImageBytes dataUrl CancellationToken.None
                |> Async.AwaitTask
                |> Async.RunSynchronously
            Expect.equal bytes png "decoded bytes match the original PNG"
        }

        test "loadImageBytes rejects a malformed data URL" {
            let ran =
                try
                    Vision.loadImageBytes "data:image/png;base64" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> ignore
                    false
                with _ -> true
            Expect.isTrue ran "malformed data URL should raise"
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
            let mutable lastBody = ""

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
                                use sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)
                                lastBody <- sr.ReadToEnd()
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

            // Counts image_url content parts in the captured chat payload.
            let imagePartCount () =
                let root = JsonNode.Parse lastBody :?> JsonObject
                let messages = root["messages"] :?> JsonArray
                let content = (messages[0] :?> JsonObject)["content"] :?> JsonArray
                content
                |> Seq.filter (fun p ->
                    let o = p :?> JsonObject
                    o["type"] <> null && o["type"].GetValue<string>() = "image_url")
                |> Seq.length

            try
                // Local file -> analyze_image (default prompt)
                let tmp = Path.Combine(Path.GetTempPath(), "vb-test.png")
                File.WriteAllBytes(tmp, imageBytes)
                let r1 =
                    Vision.analyzeImage [| tmp |] baseUrl "mock-model" "" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r1 "MOCK-OK" "analyze_image on a local file"
                Expect.equal (imagePartCount ()) 1 "single-image payload carries one image part"

                // Local file -> analyze_image with a custom steering prompt
                let r1b =
                    Vision.analyzeImage [| tmp |] baseUrl "mock-model" "" "Reply with exactly: STEERED-OK" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r1b "MOCK-OK" "analyze_image with a custom prompt"

                // URL -> ocr_image
                let r2 =
                    Vision.ocrImage [| baseUrl + "/image.png" |] baseUrl "mock-model" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r2 "MOCK-OK" "ocr_image from a URL"

                // MULTI-IMAGE -> analyze_image with two images (comparison/scanning)
                let r3 =
                    Vision.analyzeImage [| tmp; baseUrl + "/image.png" |] baseUrl "mock-model" "" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r3 "MOCK-OK" "analyze_image with multiple images"
                Expect.equal (imagePartCount ()) 2 "multi-image payload carries one image part per image"

                // MULTI-IMAGE -> ocr_image with two images
                let r4 =
                    Vision.ocrImage [| tmp; baseUrl + "/image.png" |] baseUrl "mock-model" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal r4 "MOCK-OK" "ocr_image with multiple images"
                Expect.equal (imagePartCount ()) 2 "multi-image OCR payload carries one image part per image"
            finally
                cts.Cancel()
                listener.Stop()
        )

        testCase "batches > threshold images, but one tool call = one request by default" (fun () ->
            // Phase 1 — split safety valve: when the threshold is forced low
            // (VISION_MAX_IMAGES_PER_REQUEST=4), a call with more images than the
            // threshold must split into several smaller chat requests (each <= threshold
            // image parts) and the labeled replies concatenated; every image is sent
            // exactly once and no two image_url parts are adjacent (frame-merge fix).
            // Phase 2 — default: with the high default threshold, a 6-image call is a
            // SINGLE chat request carrying every image (the fix for the "double request
            // for one mcp tool" the user observed). Both phases run in one testCase so
            // the env change can't race with other tests (Expecto runs testCases in
            // parallel); no other test uses >4 images, so the env is safe either way.
            Environment.SetEnvironmentVariable("VISION_MAX_IMAGES_PER_REQUEST", "4")
            use cts = new CancellationTokenSource()
            use listener = new HttpListener()
            let port = 8124
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()

            let imageBytes = makePng 64 64
            let baseUrl = sprintf "http://127.0.0.1:%d" port
            let chatCount = ref 0
            let maxParts = ref 0
            let totalParts = ref 0
            let maxConsec = ref 0

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
                                use sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)
                                let body = sr.ReadToEnd()
                                let root = JsonNode.Parse body :?> JsonObject
                                let messages = root["messages"] :?> JsonArray
                                let content = (messages[0] :?> JsonObject)["content"] :?> JsonArray
                                let parts =
                                    content
                                    |> Seq.filter (fun p ->
                                        let o = p :?> JsonObject
                                        o["type"] <> null && o["type"].GetValue<string>() = "image_url")
                                    |> Seq.length
                                // Longest run of CONSECUTIVE image_url parts: must stay 1 so the
                                // text separator (the llama.cpp frame-merge fix) is present
                                // between every pair of images.
                                let consec =
                                    content
                                    |> Seq.fold
                                        (fun (run, best) p ->
                                            let o = p :?> JsonObject
                                            let isImg = o["type"] <> null && o["type"].GetValue<string>() = "image_url"
                                            let nr = if isImg then run + 1 else 0
                                            (nr, max best nr))
                                        (0, 0)
                                    |> snd
                                maxConsec.Value <- max maxConsec.Value consec
                                chatCount.Value <- chatCount.Value + 1
                                maxParts.Value <- max maxParts.Value parts
                                totalParts.Value <- totalParts.Value + parts
                                let resp = """{"choices":[{"message":{"content":"MOCK-OK"}}]}"""
                                let bytes = Encoding.UTF8.GetBytes resp
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
                let urls = [| for _ in 1 .. 6 -> baseUrl + "/image.png" |]
                // Phase 1: threshold 4 -> 2 requests, every image sent once, no adjacent images.
                let r1 =
                    Vision.analyzeImage urls baseUrl "mock-model" "" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal chatCount.Value 2 "6 images -> 2 chat requests (threshold 4)"
                Expect.isTrue (maxParts.Value <= 4) "each request carries at most 4 image parts"
                Expect.equal totalParts.Value 6 "every image sent exactly once across requests"
                Expect.equal maxConsec.Value 1 "text separator between images (llama.cpp frame-merge fix)"
                Expect.isTrue (r1.Contains("MOCK-OK")) "concatenated reply contains the mock answer"

                // Phase 2: default threshold -> a single request carrying all 6 images.
                chatCount.Value <- 0
                maxParts.Value <- 0
                totalParts.Value <- 0
                maxConsec.Value <- 0
                Environment.SetEnvironmentVariable("VISION_MAX_IMAGES_PER_REQUEST", null)
                let r2 =
                    Vision.analyzeImage urls baseUrl "mock-model" "" "" CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Expect.equal chatCount.Value 1 "6 images -> exactly 1 chat request by default"
                Expect.equal maxParts.Value 6 "single request carries all 6 image parts"
                Expect.equal totalParts.Value 6 "every image sent exactly once"
                Expect.isTrue (r2.Contains("MOCK-OK")) "reply contains the mock answer"
            finally
                cts.Cancel()
                listener.Stop()
                Environment.SetEnvironmentVariable("VISION_MAX_IMAGES_PER_REQUEST", null)
        )
    ]

[<Tests>]
let proxyTests =
    let requestJson =
        """{
          "model": "m",
          "messages": [
            {"role": "user", "content": "hello"},
            {"role": "user", "content": [
              {"type": "text", "text": "t"},
              {"type": "image_url", "image_url": {"url": "http://a/1.png"}},
              {"type": "image_url", "image_url": "data:image/png;base64,AAAA"}
            ]},
            {"role": "assistant", "content": [
              {"type": "image_url", "image_url": {"url": "http://b/2.png"}}
            ]}
          ]
        }"""

    let parse (s: string) = JsonNode.Parse(s) :?> JsonObject

    testList "Proxy" [
        test "collectImageUrls finds every image part in reading order" {
            let urls = Proxy.collectImageUrls (parse requestJson)
            Expect.equal urls [ "http://a/1.png"; "data:image/png;base64,AAAA"; "http://b/2.png" ] "urls across messages"
        }

        test "rewriteWithImages replaces every image part with an indexed text part" {
            let rewritten = Proxy.rewriteWithImages (parse requestJson) [ "d1"; "d2"; "d3" ]
            Expect.equal (rewritten["model"].GetValue<string>()) "m" "model untouched"
            Expect.equal (Proxy.collectImageUrls rewritten) [] "no image parts remain"

            let msgs = rewritten["messages"] :?> JsonArray
            // plain-string message untouched
            let content0 = (msgs[0] :?> JsonObject)["content"] :?> JsonValue
            Expect.equal (content0.GetValue<string>()) "hello" "plain-text message untouched"

            // message with text + 2 images: parts become [text, [Image 1], [Image 2]]
            let content1 = (msgs[1] :?> JsonObject)["content"] :?> JsonArray
            Expect.equal content1.Count 3 "2 image parts replaced in place"
            let p0 = content1[0] :?> JsonObject
            Expect.equal (p0["type"].GetValue<string>()) "text" "first part stays text"
            Expect.equal (p0["text"].GetValue<string>()) "t" "first part text kept"
            let p1 = content1[1] :?> JsonObject
            Expect.equal (p1["type"].GetValue<string>()) "text" "image 1 became text"
            Expect.equal (p1["text"].GetValue<string>()) "[Image 1: d1]" "image 1 marker"
            let p2 = content1[2] :?> JsonObject
            Expect.equal (p2["text"].GetValue<string>()) "[Image 2: d2]" "image 2 marker"

            // assistant message with 1 image
            let content2 = (msgs[2] :?> JsonObject)["content"] :?> JsonArray
            Expect.equal content2.Count 1 "1 image part replaced"
            let p3 = content2[0] :?> JsonObject
            Expect.equal (p3["text"].GetValue<string>()) "[Image 3: d3]" "image 3 marker"
        }

        test "rewriteWithImages marks missing descriptions unavailable" {
            let rewritten = Proxy.rewriteWithImages (parse requestJson) [ "d1" ]
            let msgs = rewritten["messages"] :?> JsonArray
            let content1 = (msgs[1] :?> JsonObject)["content"] :?> JsonArray
            let p2 = content1[2] :?> JsonObject
            Expect.equal (p2["text"].GetValue<string>()) "[Image 2: [unavailable]]" "missing description noted"
        }

        test "prepareForward pins the LLM model and stream flag" {
            let cfg =
                { LlmEndpoint = "http://llm/v1"; LlmModel = "llm-model"; LlmApiKey = ""
                  VlmEndpoint = "http://vlm/v1"; VlmModel = "vlm-model"; VlmApiKey = ""
                  Port = 1 }
            let fwd = Proxy.prepareForward (parse """{"model":"client-model","stream":true}""") cfg true
            Expect.equal (fwd["model"].GetValue<string>()) "llm-model" "model pinned to LLM"
            Expect.equal (fwd["stream"].GetValue<bool>()) true "stream pinned"
        }
    ]
