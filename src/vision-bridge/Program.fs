namespace VisionBridge

open System
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

/// Tool arguments for analyze_image: an arbitrary number of images (local file
/// paths, http(s) URLs, or data URLs). endpoint/model/api_key/prompt are optional
/// and fall back to their OPENAI_* environment variables / default prompts.
type AnalyzeArgs =
    { images: string[]
      endpoint: string option
      model: string option
      api_key: string option
      prompt: string option }

/// Tool arguments for ocr_image: an arbitrary number of images.
/// endpoint/model/api_key are optional and fall back to their OPENAI_* environment variables.
type OcrArgs =
    { images: string[]
      endpoint: string option
      model: string option
      api_key: string option }

module Program =

    /// Sets the OPENAI_* / VLM_* / PROXY_PORT env vars from CLI flags. CLI values
    /// take priority over any pre-existing environment variables. Returns the argv
    /// with our flags stripped (the FsMcp server reads no host builder args).
    let private applyCliConfig (argv: string[]) : string[] =
        let kept = ResizeArray<string>()
        let rec go i =
            if i < argv.Length then
                let valueOf () =
                    if i + 1 >= argv.Length then
                        failwithf "Missing value for %s" argv.[i]
                    argv.[i + 1]
                match argv.[i] with
                | "--endpoint" | "-e" ->
                    Environment.SetEnvironmentVariable("OPENAI_BASE_URL", valueOf ())
                    go (i + 2)
                | "--model" | "-m" ->
                    Environment.SetEnvironmentVariable("OPENAI_MODEL", valueOf ())
                    go (i + 2)
                | "--api-key" | "-k" ->
                    Environment.SetEnvironmentVariable("OPENAI_API_KEY", valueOf ())
                    go (i + 2)
                | "--vlm-endpoint" | "-ve" ->
                    Environment.SetEnvironmentVariable("VLM_BASE_URL", valueOf ())
                    go (i + 2)
                | "--vlm-model" | "-vm" ->
                    Environment.SetEnvironmentVariable("VLM_MODEL", valueOf ())
                    go (i + 2)
                | "--vlm-api-key" | "-vk" ->
                    Environment.SetEnvironmentVariable("VLM_API_KEY", valueOf ())
                    go (i + 2)
                | "--port" | "-p" ->
                    Environment.SetEnvironmentVariable("PROXY_PORT", valueOf ())
                    go (i + 2)
                | other ->
                    kept.Add other
                    go (i + 1)
        go 0
        kept.ToArray()

    /// Handles analyze_image: describes every image (comparison, scanning several
    /// pages, ...) with the vision model and returns the text reply.
    let private analyzeHandler (args: AnalyzeArgs) : Task<Result<Content list, McpError>> = task {
        try
            let! text =
                Vision.analyzeImage
                    args.images
                    (defaultArg args.endpoint "")
                    (defaultArg args.model "")
                    (defaultArg args.api_key "")
                    (defaultArg args.prompt "")
                    CancellationToken.None
            return Ok [ Content.text text ]
        with ex ->
            return Error (TransportError ex.Message)
    }

    /// Handles ocr_image: extracts all text from every image and returns it.
    let private ocrHandler (args: OcrArgs) : Task<Result<Content list, McpError>> = task {
        try
            let! text =
                Vision.ocrImage
                    args.images
                    (defaultArg args.endpoint "")
                    (defaultArg args.model "")
                    (defaultArg args.api_key "")
                    CancellationToken.None
            return Ok [ Content.text text ]
        with ex ->
            return Error (TransportError ex.Message)
    }

    [<EntryPoint>]
    let main argv =
        // CLI config flags (--endpoint/--model/--api-key/--vlm-*/--port) override the env vars.
        applyCliConfig argv |> ignore

        if argv |> Array.exists (fun a -> a = "--proxy") then
            // OpenAI-compatible proxy: rewrites images into guided VLM descriptions
            // for a text-only LLM upstream (FAKE ProxyTest target).
            Proxy.runProxy ()
        elif argv |> Array.exists (fun a -> a = "--smoke") then
            // Functional smoke test against a live endpoint (FAKE SmokeTest target).
            Smoke.runSmoke ()
        else
            // FsMcp stdio server: stdout is reserved for the MCP protocol, logs go to stderr.
            let server =
                mcpServer {
                    name "vision-bridge"
                    version "1.0.0"
                    tool (
                        TypedTool.define<AnalyzeArgs>
                            "analyze_image"
                            "Analyzes one or more images (local file paths, http(s) URLs, or data URLs) and returns a detailed textual description of their visual content."
                            analyzeHandler
                        |> unwrapResult
                    )
                    tool (
                        TypedTool.define<OcrArgs>
                            "ocr_image"
                            "Extracts all text from one or more images (local file paths, http(s) URLs, or data URLs) using optical character recognition (OCR)."
                            ocrHandler
                        |> unwrapResult
                    )
                    useStdio
                }
            Server.run server |> fun t -> t.GetAwaiter().GetResult()
            0
