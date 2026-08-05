# vision-bridge

An F# MCP stdio server that adds vision capabilities to a pure text-to-text LLM.
It exposes two tools over the Model Context Protocol (C# SDK 2.0):

- **`analyze_image`** — takes an image (local file path or http(s) URL) and returns a
  detailed textual description of its visual content.
- **`ocr_image`** — takes an image (local file path or http(s) URL) and returns the text
  extracted from it (OCR).

The server talks to any OpenAI-compatible chat/completions endpoint that accepts
`image_url` content parts (vision models).

## Configuration

The server needs an OpenAI-compatible endpoint and a vision model. Set them as
environment variables, or pass them per-call as tool arguments:

| Setting | Env var | Tool argument |
|---|---|---|
| Endpoint (e.g. `http://localhost:8080/v1`) | `OPENAI_BASE_URL` | `endpoint` |
| Model (e.g. `qwen3.6-moe:instruct`) | `OPENAI_MODEL` | `model` |

Tool arguments take priority; the environment variables are the fallback.

## Running

```sh
dotnet run --project src/vision-bridge/vision-bridge.fsproj
```

The server speaks MCP over stdio (newline-delimited JSON-RPC). Logs go to stderr;
stdout is reserved for the protocol.

## GitHub Package (dotnet tool)

`vision-bridge` is packaged as a .NET **global tool** and published to GitHub Packages
as a NuGet package. This is the recommended way to distribute it to MCP clients:
install once, then reference the `vision-bridge` command in `mcpServers`.

### Publish to GitHub Packages

The repo's CD workflow (`.github/workflows/publish.yml`) packs and pushes to
`https://nuget.pkg.github.com/fradav/index.json` on every push to `main` (or a `v*`
tag), then installs and smoke-tests the published tool. To publish manually:

```sh
dotnet run --project Build.fsproj -- -t Pack   # -> nupkg/vision-bridge.<version>.nupkg
GITHUB_PACKAGES_URL=https://nuget.pkg.github.com/fradav/index.json \
GITHUB_TOKEN=<token> \
  dotnet run --project Build.fsproj -- -t Publish
```

### Install the tool from GitHub Packages

Add the feed and install the global tool:

```sh
dotnet nuget add source https://nuget.pkg.github.com/fradav/index.json \
  --name GitHubPackages --username <user> --password <token> --store-password-in-clear-text
dotnet tool install -g vision-bridge
```

Verify it works: with `OPENAI_BASE_URL`/`OPENAI_MODEL` set, `vision-bridge --smoke`
runs the real-endpoint smoke test.

### Call it from mcpServers

Once installed, register the tool in any client's `mcpServers` map:

```json
{
  "mcpServers": {
    "vision-bridge": {
      "type": "stdio",
      "command": "vision-bridge",
      "args": [],
      "env": {
        "OPENAI_BASE_URL": "http://localhost:8080/v1",
        "OPENAI_MODEL": "qwen3.6-moe:instruct"
      }
    }
  }
}
```

The `args` are optional — the tool reads the endpoint/model from `env` (or per-call
tool arguments). For the source checkout instead, use the dev config in `.mcp.json`
(see below).

## Registering with MCP clients

The repo ships a standard `mcpServers` entry in `.mcp.json` (dev form). It launches
from the source checkout with `dotnet run --no-build` and points at the endpoint/model
from AGENTS.md (override via the `env` block or per-call tool arguments). For the
installed-tool form, use the `command: "vision-bridge"` snippet above.

**Claude Code** — reads `.mcp.json` from the project root automatically. To enable it:

```sh
claude mcp add vision-bridge --type stdio --config .mcp.json
```

**Zed** — add the same entry under `mcp_servers` (or `context_servers`) in your
`~/.config/zed/settings.json` or the project `.zed/settings.json`:

```json
{
  "mcp_servers": {
    "vision-bridge": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "src/vision-bridge/vision-bridge.fsproj"],
      "env": {
        "OPENAI_BASE_URL": "http://localhost:8080/v1",
        "OPENAI_MODEL": "qwen3.6-moe:instruct"
      }
    }
  }
}
```

**GitHub Copilot** — add the same `vision-bridge` entry to the `mcpServers` map in
your Copilot MCP config (e.g. `.github/copilot-mcp.json` or the editor settings):

```json
{
  "mcpServers": {
    "vision-bridge": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "src/vision-bridge/vision-bridge.fsproj"],
      "env": {
        "OPENAI_BASE_URL": "http://localhost:8080/v1",
        "OPENAI_MODEL": "qwen3.6-moe:instruct"
      }
    }
  }
}
```

> Note: `--no-build` requires the project to be built once (`dotnet build` or the
> `BuildRelease` FAKE target). If you'd rather not rely on a prior build, drop
> `--no-build` — `dotnet run` will compile on first launch.

## Building & testing

This is a FAKE/Paket project.

```sh
dotnet tool restore
dotnet paket install
dotnet run --project Build.fsproj -- -t Test   # clean + run the Expecto suite
```

The test suite includes unit tests for image loading/validation/downscaling and an
offline end-to-end test that runs both tools against a local mock OpenAI endpoint.

## Real-endpoint smoke test (FAKE task)

A functional smoke test drives the real vision pipeline against a live
OpenAI-compatible endpoint, using **real images** checked into `samples/`:

- `samples/photo.jpg` — a real photograph, used by `analyze_image`.
- `samples/text-sign.jpg` — a real street sign, used by `ocr_image`.

Run it (requires a live endpoint + model via env vars):

```sh
OPENAI_BASE_URL=http://localhost:8080/v1 OPENAI_MODEL=qwen3.6-moe:instruct \
  dotnet run --project Build.fsproj -- -t SmokeTest
```

The task builds the app, then runs `vision-bridge --smoke`. For each tool it
exercises **both** input modes against real images: a local file path and an
http(s) URL (the samples are served by a short-lived local HTTP server). It
asserts that `analyze_image` returns a non-empty description of the photo and
that `ocr_image` extracts the sign's text (it checks for the known `TOURVILLE`
substring). It exits non-zero on any failure.
