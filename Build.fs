open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open System
open System.IO
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

// ——— Context & Paths ———
let repoRoot = __SOURCE_DIRECTORY__
let slnFile = Path.Combine(repoRoot, "src.slnx")

// Initialize FAKE execution context (required for dedicated build projects)
Context.setExecutionContext (
    Context.RuntimeContext.Fake(
        Context.FakeExecutionContext.Create false "Build.fs" (Environment.GetCommandLineArgs() |> Array.skip 1 |> Array.toList)
    )
)

// ——— Targets ———

Target.create "Clean" (fun _ ->
    DotNet.exec (fun p -> p) "clean" slnFile |> ignore
    Trace.log "Clean done")

Target.create "BuildRelease" (fun _ ->
    !!"src/**/*.fsproj"
    -- "src/**/*.Tests.fsproj"
    |> Seq.iter (fun proj ->
        Trace.log (sprintf "Building: %s" proj)
        DotNet.build (fun p -> { p with Configuration = DotNet.Release }) proj)

    Trace.log "BuildRelease done")

Target.create "Test" (fun _ ->
    let exitCode =
        !!"src/**/*.Tests.fsproj"
        |> Seq.map (fun proj ->
            let result =
                DotNet.exec
                    (fun p -> { p with WorkingDirectory = repoRoot })
                    "test"
                    (sprintf "%s --configuration Debug" proj)

            if not result.OK then
                Trace.traceError (sprintf "%s failed with exit code %d" proj result.ExitCode)

            result.ExitCode)
        |> Seq.filter (fun c -> c <> 0)
        |> Seq.tryHead
        |> Option.defaultValue 0

    if exitCode <> 0 then
        failwithf "Some tests failed (exit code %d)" exitCode

    Trace.log "Test done")

Target.create "SmokeTest" (fun _ ->
    // Functional smoke test: runs the real vision pipeline against a live
    // OpenAI-compatible endpoint using real images (samples/).
    // Requires OPENAI_BASE_URL and OPENAI_MODEL to be set.
    let exe = Path.Combine(repoRoot, "src/vision-bridge/bin/Release/net10.0/vision-bridge.dll")
    Trace.log (sprintf "Smoke testing: %s --smoke" exe)

    let result =
        DotNet.exec
            (fun p -> { p with WorkingDirectory = repoRoot })
            exe
            "--smoke"

    if not result.OK then
        failwithf "Smoke test failed (exit code %d)" result.ExitCode

    Trace.log "SmokeTest done")

Target.create "ProxyTest" (fun _ ->
    // Functional test of the OpenAI-compatible proxy: starts vision-bridge --proxy,
    // sends a chat request carrying TWO images (a photo and a street sign, as data
    // URLs), and checks the LLM answer uses the VLM's description of the sign.
    let exe = Path.Combine(repoRoot, "src/vision-bridge/bin/Release/net10.0/vision-bridge.dll")
    let llmBase = Environment.GetEnvironmentVariable "OPENAI_BASE_URL"
    let llmModel = Environment.GetEnvironmentVariable "OPENAI_MODEL"
    if String.IsNullOrWhiteSpace llmBase || String.IsNullOrWhiteSpace llmModel then
        failwith "ProxyTest requires OPENAI_BASE_URL and OPENAI_MODEL (the LLM upstream)"
    let vlmBase = Environment.GetEnvironmentVariable "VLM_BASE_URL" |> fun v -> if String.IsNullOrWhiteSpace v then llmBase else v
    let vlmModel = Environment.GetEnvironmentVariable "VLM_MODEL" |> fun v -> if String.IsNullOrWhiteSpace v then llmModel else v

    let port =
        let l = new TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let p = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        p

    let dataUrl (path: string) =
        let b64 = Convert.ToBase64String(File.ReadAllBytes path)
        sprintf "data:image/jpeg;base64,%s" b64

    let photo = Path.Combine(repoRoot, "samples/photo.jpg")
    let sign = Path.Combine(repoRoot, "samples/text-sign.jpg")

    Trace.log (sprintf "Proxy testing: %s --proxy --port %d (LLM=%s/%s VLM=%s/%s)" exe port llmBase llmModel vlmBase vlmModel)

    let psi = ProcessStartInfo()
    psi.FileName <- "dotnet"
    psi.Arguments <- sprintf "\"%s\" --proxy --port %d --endpoint %s --model %s --vlm-endpoint %s --vlm-model %s" exe port llmBase llmModel vlmBase vlmModel
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi

    try
        use hc = new HttpClient()
        let mutable healthy = false
        for _ in 1 .. 60 do
            if not healthy then
                try
                    use resp = hc.GetAsync(sprintf "http://127.0.0.1:%d/health" port) |> Async.AwaitTask |> Async.RunSynchronously
                    healthy <- resp.StatusCode = HttpStatusCode.OK
                with _ -> ()
                Thread.Sleep 250
        if not healthy then
            failwithf "Proxy did not become healthy on port %d" port

        // Chat request with TWO images (photo + street sign), as data URLs.
        let payload = JsonObject()
        payload["model"] <- "visionbridge"
        let messages = JsonArray()
        let userMsg = JsonObject()
        userMsg["role"] <- "user"
        let content = JsonArray()
        let textPart = JsonObject()
        textPart["type"] <- "text"
        textPart["text"] <- "Here are descriptions of two images. The second image shows a street sign. What street name appears on the sign? Reply with the street name only."
        let img1 = JsonObject()
        img1["type"] <- "image_url"
        let iu1 = JsonObject()
        iu1["url"] <- dataUrl photo
        img1["image_url"] <- iu1
        let img2 = JsonObject()
        img2["type"] <- "image_url"
        let iu2 = JsonObject()
        iu2["url"] <- dataUrl sign
        img2["image_url"] <- iu2
        content.Add textPart |> ignore
        content.Add img1 |> ignore
        content.Add img2 |> ignore
        userMsg["content"] <- content
        messages.Add userMsg |> ignore
        payload["messages"] <- messages
        payload["stream"] <- false
        payload["max_tokens"] <- 512

        use body = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        use resp = hc.PostAsync(sprintf "http://127.0.0.1:%d/v1/chat/completions" port, body) |> Async.AwaitTask |> Async.RunSynchronously
        let bodyText = resp.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
        if not resp.IsSuccessStatusCode then
            failwithf "Proxy chat returned %d: %s" (int resp.StatusCode) bodyText
        use doc = JsonDocument.Parse bodyText
        let answer =
            doc.RootElement.GetProperty("choices").EnumerateArray()
            |> Seq.head
            |> fun c -> c.GetProperty("message").GetProperty("content").GetString()
        Trace.log (sprintf "ProxyTest answer: %s" answer)
        if not (answer.Contains("TOURVILLE", StringComparison.OrdinalIgnoreCase)) then
            failwithf "ProxyTest: expected TOURVILLE in the answer, got: %s" answer

        // Streaming request with the same two images.
        payload["stream"] <- true
        use body2 = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        use resp2 = hc.PostAsync(sprintf "http://127.0.0.1:%d/v1/chat/completions" port, body2) |> Async.AwaitTask |> Async.RunSynchronously
        let sse = resp2.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
        if not (sse.Contains("data: ") && sse.Contains("[DONE]")) then
            failwithf "ProxyTest: streaming response missing data/[DONE]: %s" sse
        // The streamed tokens may split a word across chunks, so accumulate the
        // content deltas before asserting on the answer text.
        let accumulated =
            sse.Split('\n')
            |> Seq.choose (fun line ->
                if line.StartsWith("data: [DONE]") then None
                elif line.StartsWith("data: ") then
                    let json = line.Substring(6)
                    try
                        use d = JsonDocument.Parse json
                        let c = d.RootElement.GetProperty("choices").EnumerateArray() |> Seq.head
                        let delta = c.GetProperty("delta")
                        let ok, content = delta.TryGetProperty("content")
                        if ok then Some(content.GetString()) else None
                    with _ -> None
                else None)
            |> String.concat ""
        if not (accumulated.Contains("TOURVILLE", StringComparison.OrdinalIgnoreCase)) then
            failwithf "ProxyTest: streamed answer missing TOURVILLE: %s" accumulated
        Trace.log "ProxyTest: PASS"
    finally
        try p.Kill() with _ -> ()
        p.WaitForExit()
    )

Target.create "Pack" (fun _ ->
    let version =
        Environment.GetEnvironmentVariable "PACK_VERSION"
        |> function
           | null | "" -> "1.0.0"
           | v -> v

    let outputDir = Path.Combine(repoRoot, "nupkg")
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore

    Trace.log (sprintf "Packing version: %s" version)
    Trace.log (sprintf "Output directory: %s" outputDir)

    !!"src/**/*.fsproj"
    -- "src/**/*.Tests.fsproj"
    |> Seq.iter (fun proj ->
        Trace.log (sprintf "Packing: %s" proj)
        let result =
            DotNet.exec
                (fun p -> { p with WorkingDirectory = repoRoot })
                "pack"
                (sprintf "%s -c Release -o \"%s\" -p:PackageVersion=%s -p:Version=%s -p:IsPacking=true --no-build --no-restore" proj outputDir version version)

        if not result.OK then
            failwithf "Pack failed for %s with exit code %d" proj result.ExitCode)

    let generated = Directory.GetFiles(outputDir, "*.nupkg")
    if generated.Length = 0 then
        failwithf "Pack produced no .nupkg files in %s" outputDir
    generated |> Array.iter (fun f -> Trace.log (sprintf "Generated: %s" f))

    Trace.log "Pack done")

Target.create "Publish" (fun _ ->
    let source =
        Environment.GetEnvironmentVariable "GITHUB_PACKAGES_URL"
        |> function
           | null | "" -> failwith "GITHUB_PACKAGES_URL environment variable not set"
           | v -> v

    let apiKey =
        Environment.GetEnvironmentVariable "GITHUB_TOKEN"
        |> function
           | null | "" -> failwith "GITHUB_TOKEN environment variable not set"
           | v -> v

    let outputDir = Path.Combine(repoRoot, "nupkg")

    let nupkgs = Directory.GetFiles(outputDir, "*.nupkg")
    if nupkgs.Length = 0 then
        failwithf "No .nupkg files found in %s to publish" outputDir

    nupkgs
    |> Array.iter (fun nupkg ->
        Trace.log (sprintf "Publishing: %s" nupkg)
        let result =
            DotNet.exec
                (fun p -> { p with WorkingDirectory = repoRoot })
                "nuget"
                (sprintf "push \"%s\" --source \"%s\" --api-key \"%s\" --skip-duplicate" nupkg source apiKey)

        if not result.OK then
            failwithf "Publish failed for %s with exit code %d" nupkg result.ExitCode)

    Trace.log "Publish done")

Target.create "Default" ignore

// ——— Dependencies ———

"Clean" ==> "Test" |> ignore
"BuildRelease" ==> "SmokeTest" |> ignore
"BuildRelease" ==> "ProxyTest" |> ignore
"BuildRelease" ==> "Pack" ==> "Publish" |> ignore

Target.runOrDefaultWithArguments "Default"
