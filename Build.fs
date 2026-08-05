open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open System
open System.IO

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
        !!"/Users/fradav/Documents/Dev/AITools/mcp/vision-bridge/src/**/*.Tests.fsproj"
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

    !!"/Users/fradav/Documents/Dev/AITools/mcp/vision-bridge/src/**/*.fsproj"
    -- "/Users/fradav/Documents/Dev/AITools/mcp/vision-bridge/src/**/*.Tests.fsproj"
    |> Seq.iter (fun proj ->
        Trace.log (sprintf "Packing: %s" proj)
        let result =
            DotNet.exec
                (fun p -> { p with WorkingDirectory = repoRoot })
                "pack"
                (sprintf "%s -c Release -o \"%s\" -p:PackageVersion=%s -p:Version=%s -p:IsPacking=true --no-build --no-restore" proj outputDir version version)

        if not result.OK then
            failwithf "Pack failed for %s with exit code %d" proj result.ExitCode)

    if Directory.Exists outputDir then
        Directory.GetFiles(outputDir, "*.nupkg")
        |> Array.iter (fun f -> Trace.log (sprintf "Generated: %s" f))
    else
        failwithf "Output directory %s does not exist after packing" outputDir

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

    Directory.GetFiles(outputDir, "*.nupkg")
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
"BuildRelease" ==> "Pack" ==> "Publish" |> ignore

Target.runOrDefaultWithArguments "Default"
