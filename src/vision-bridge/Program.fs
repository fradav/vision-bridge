namespace VisionBridge

open System
open System.ComponentModel
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server

/// MCP tools exposed by the vision-bridge stdio server.
[<McpServerToolType>]
type VisionTools() =

    [<McpServerTool; Description("Analyzes an image or image URL and returns a detailed textual description of its visual content.")>]
    static member AnalyzeImage
        (
            [<Description("Path to a local image file, or an http(s) URL of an image.")>] image: string,
            [<Description("OpenAI-compatible endpoint, e.g. https://api.openai.com/v1. If empty, uses OPENAI_BASE_URL.")>] ?endpoint: string,
            [<Description("Vision model name, e.g. gpt-4o-mini. If empty, uses OPENAI_MODEL.")>] ?model: string,
            [<Description("API key for the endpoint. If empty, uses OPENAI_API_KEY.")>] ?api_key: string
        ) : Task<string> =
        Vision.analyzeImage image (defaultArg endpoint "") (defaultArg model "") (defaultArg api_key "") CancellationToken.None

    [<McpServerTool; Description("Extracts all text from an image or image URL using Optical Character Recognition (OCR).")>]
    static member OcrImage
        (
            [<Description("Path to a local image file, or an http(s) URL of an image.")>] image: string,
            [<Description("OpenAI-compatible endpoint, e.g. https://api.openai.com/v1. If empty, uses OPENAI_BASE_URL.")>] ?endpoint: string,
            [<Description("Vision model name, e.g. gpt-4o-mini. If empty, uses OPENAI_MODEL.")>] ?model: string,
            [<Description("API key for the endpoint. If empty, uses OPENAI_API_KEY.")>] ?api_key: string
        ) : Task<string> =
        Vision.ocrImage image (defaultArg endpoint "") (defaultArg model "") (defaultArg api_key "") CancellationToken.None

module Program =

    [<EntryPoint>]
    let main argv =
        if argv |> Array.exists (fun a -> a = "--smoke") then
            // Functional smoke test against a live endpoint (FAKE SmokeTest target).
            Smoke.runSmoke ()
        else
            let builder = Host.CreateApplicationBuilder argv

            // All logs must go to stderr — stdout is reserved for the MCP stdio protocol.
            builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore

            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<VisionTools>()
                |> ignore

            builder.Build().RunAsync().GetAwaiter().GetResult()
            0
