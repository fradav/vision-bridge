# vision-bridge

An F# MCP stdio server that adds vision capabilities to a pure text-to-text LLM.
It exposes two tools over the Model Context Protocol, built with the **FsMcp** F#
library (which wraps the official C# MCP SDK):

- **`analyze_image`** — takes one or more images (local file paths, http(s) URLs, or
  data URLs) and returns a detailed textual description of their visual content.
- **`ocr_image`** — takes one or more images (local file paths, http(s) URLs, or data
  URLs) and returns the text extracted from them (OCR).

The server talks to any OpenAI-compatible chat/completions endpoint that accepts
`image_url` content parts (vision models).

`analyze_image` also accepts an optional **`prompt`** argument that replaces the default
analysis instruction — use it to steer/guide the analysis (e.g. *"Count the objects in
this image"*, *"Describe the colors only"*). If omitted or empty, the default detailed
description prompt is used. `ocr_image` does not take a prompt.

## Configuration

The server needs an OpenAI-compatible endpoint and a vision model. Set them as
environment variables, or pass them per-call as tool arguments:

| Setting | Env var | Tool argument |
|---|---|---|
| Endpoint (e.g. `http://localhost:8080/v1`) | `OPENAI_BASE_URL` | `endpoint` |
| Model (e.g. `qwen3.6-moe:instruct`) | `OPENAI_MODEL` | `model` |
| API key (optional, e.g. `sk-...`) | `OPENAI_API_KEY` | `api_key` |

In the MCP tool schema only `images` is required — an **array** of images (local file paths, http(s) URLs, or data URLs). `endpoint`, `model`, `api_key` and (for `analyze_image`) `prompt` are optional and fall back to their `OPENAI_*` environment variables when omitted. Tool arguments take priority; the environment variables are the fallback.

Both tools accept an **arbitrary number of images** — use it for comparisons, scanning
several pages, checking multiple screenshots, and so on. `analyze_image` describes each
image in order and labels them `Image 1:`, `Image 2:`, ... so the model can compare them;
`ocr_image` extracts the text of each image and labels the extracted text the same way.

```json
{
  "images": [
    "/tmp/page1.png",
    "https://example.com/page2.jpg",
    "data:image/jpeg;base64,..."
  ],
  "prompt": "Compare the two pages and list the differences"
}
```

You can also configure the server itself at startup, via CLI flags or environment
variables:

| Flag | Env var | Purpose |
|---|---|---|
| `--endpoint` / `-e` | `OPENAI_BASE_URL` | OpenAI-compatible endpoint |
| `--model` / `-m` | `OPENAI_MODEL` | Vision model name |
| `--api-key` / `-k` | `OPENAI_API_KEY` | API key (optional) |

CLI flags override the environment variables. Pass them in the `mcpServers` `args`
(installed-tool form) or `args` of the `dotnet run` command (dev form).

## Running

```sh
dotnet run --project src/vision-bridge/vision-bridge.fsproj
```

The server speaks MCP over stdio (newline-delimited JSON-RPC). Logs go to stderr;
stdout is reserved for the protocol.

## Proxy mode (OpenAI-compatible wrapper)

`vision-bridge --proxy` runs an OpenAI-compatible **server wrapper** that makes a
text-only LLM "vision-aware". It takes two OpenAI-compatible upstreams — an LLM
and a VLM — and rewrites every `image_url` content part of a chat request into a
guided VLM description before forwarding it to the LLM, so the LLM only ever sees
text. An **arbitrary number of images** per request is supported (comparison,
scanning several pages, ...); images are described by the VLM in parallel and each
keeps its reading-order index `[Image N: ...]` across the whole request so the LLM
can reference and compare them.

### Proxy configuration

| Setting | Env var | CLI flag |
|---|---|---|
| LLM upstream endpoint | `OPENAI_BASE_URL` | `--endpoint` / `-e` |
| LLM model | `OPENAI_MODEL` | `--model` / `-m` |
| LLM API key (optional) | `OPENAI_API_KEY` | `--api-key` / `-k` |
| VLM upstream endpoint | `VLM_BASE_URL` | `--vlm-endpoint` / `-ve` |
| VLM model | `VLM_MODEL` | `--vlm-model` / `-vm` |
| VLM API key (optional) | `VLM_API_KEY` | `--vlm-api-key` / `-vk` |
| Listen port (default 8787) | `PROXY_PORT` | `--port` / `-p` |

```sh
vision-bridge --proxy --port 8787 \
  --endpoint http://localhost:1234/v1 --model my-text-llm \
  --vlm-endpoint http://localhost:11434/v1 --vlm-model llava:13b
```

### API surface

- `POST /v1/chat/completions` (and `/chat/completions`) — the rewritten request is
  forwarded to the LLM upstream; responses are relayed back (SSE streaming is
  passed through when `stream: true`).
- `GET /v1/models` — advertises the configured LLM model.
- `GET /health` — liveness check.

The proxy binds `127.0.0.1` only. Point any OpenAI-compatible client at
`http://127.0.0.1:<port>/v1`.

```sh
curl -s http://127.0.0.1:8787/v1/chat/completions -H 'content-type: application/json' \
  -d '{"model":"visionbridge","messages":[{"role":"user","content":[
    {"type":"text","text":"Compare the two images."},
    {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,..."}},
    {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,..."}}
  ]}]}'
```

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

Add the feed and install the global tool. The `<token>` must be a GitHub PAT with
the `read:packages` scope (a `write:packages` token also works). GitHub Packages
requires authentication even for public repos:

```sh
dotnet nuget add source https://nuget.pkg.github.com/fradav/index.json \
  --name GitHubPackages --username <user> --password <token> --store-password-in-clear-text
dotnet tool install -g vision-bridge
```

> Note: the CD workflow's `test-tools` job installs from this feed automatically
> (its `GITHUB_TOKEN` has the packages scope), so this is verified on every publish.

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
        "OPENAI_MODEL": "qwen3.6-moe:instruct",
        "OPENAI_API_KEY": ""
      }
    }
  }
}
```

The `args` are optional — the tool reads the endpoint/model from `env` (or per-call
tool arguments). You can also configure the server via CLI flags in `args` instead of
`env` (CLI overrides env):

```json
{
  "mcpServers": {
    "vision-bridge": {
      "type": "stdio",
      "command": "vision-bridge",
      "args": ["--endpoint", "http://localhost:8080/v1", "--model", "qwen3.6-moe:instruct"],
      "env": {}
    }
  }
}
```

For the source checkout instead, use the dev config in `.mcp.json`
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
        "OPENAI_MODEL": "qwen3.6-moe:instruct",
        "OPENAI_API_KEY": ""
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
        "OPENAI_MODEL": "qwen3.6-moe:instruct",
        "OPENAI_API_KEY": ""
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
offline end-to-end test that runs both tools against a local mock OpenAI endpoint,
including multi-image payloads (one `image_url` part per image).

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

`OPENAI_API_KEY` is optional — set it only if the endpoint requires a key.

The task builds the app, then runs `vision-bridge --smoke`. For each tool it
exercises **both** input modes against real images: a local file path and an
http(s) URL (the samples are served by a short-lived local HTTP server). It also
exercises **multi-image** calls: `analyze_image` with photo + street sign (the
answer must mention the sign's `TOURVILLE` text) and `ocr_image` with sign + photo
(the extracted text must still contain `TOURVILLE`). It asserts that
`analyze_image` returns a non-empty description of the photo, that a custom
steering prompt is honored, and that `ocr_image` extracts the sign's text. It
exits non-zero on any failure.
