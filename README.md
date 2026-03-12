# CCProxy

A local proxy that enables Claude Code to use OpenAI models, including those hosted on Azure. CCProxy translates between the Anthropic `/v1/messages` API and OpenAI's Responses API (`/v1/responses`), so Claude Code works seamlessly with OpenAI models like GPT-5-Codex.

Only models that support the [OpenAI Responses API](https://platform.openai.com/docs/api-reference/responses) can be used with CCProxy.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An OpenAI API key, or an Azure OpenAI deployment with a Responses API-compatible model

## Configuration

CCProxy accepts configuration via command-line arguments or environment variables. CLI arguments take precedence.

| CLI Argument   | Environment Variable   | Description                                  | Default |
|----------------|------------------------|----------------------------------------------|---------|
| `--port`       | `CCPROXY_PORT`         | Local port to listen on                      | `5186`  |
| `--endpoint`   | `CCPROXY_ENDPOINT_URL` | OpenAI or Azure OpenAI Responses API URL     | —       |
| `--key`        | `CCPROXY_API_KEY`      | API key                                      | —       |
| `--logfile`    | —                      | Path to verbose JSON log file (optional)     | —       |

### Endpoint URL examples

**OpenAI:**
```
https://api.openai.com/v1/responses
```

**Azure OpenAI:**
```
https://<your_deployment>.cognitiveservices.azure.com/openai/responses?api-version=2025-04-01-preview
```

## Quick Start

```bash
# OpenAI
dotnet run --project src/ccproxy -- \
  --endpoint https://api.openai.com/v1/responses \
  --key your-openai-api-key

# Azure OpenAI
dotnet run --project src/ccproxy -- \
  --endpoint https://your-deployment.cognitiveservices.azure.com/openai/responses?api-version=2025-04-01-preview \
  --key your-azure-api-key

# Or using environment variables
export CCPROXY_ENDPOINT_URL=https://api.openai.com/v1/responses
export CCPROXY_API_KEY=your-api-key
dotnet run --project src/ccproxy
```

## Using with Claude Code

Configure Claude Code to route requests through CCProxy by setting the following environment variables:

```bash
# Linux / macOS
export ANTHROPIC_BASE_URL=http://localhost:5186
export ANTHROPIC_DEFAULT_HAIKU_MODEL=gpt-5-nano
export ANTHROPIC_DEFAULT_SONNET_MODEL=gpt-5-mini
export ANTHROPIC_DEFAULT_OPUS_MODEL=gpt-5-codex
```

```cmd
:: Windows
set ANTHROPIC_BASE_URL=http://localhost:5186
set ANTHROPIC_DEFAULT_HAIKU_MODEL=gpt-5-nano
set ANTHROPIC_DEFAULT_SONNET_MODEL=gpt-5-mini
set ANTHROPIC_DEFAULT_OPUS_MODEL=gpt-5-codex
```

- `ANTHROPIC_BASE_URL` — Points Claude Code at the proxy instead of Anthropic's API.
- `ANTHROPIC_DEFAULT_*_MODEL` — Maps Claude Code's model tiers (Haiku, Sonnet, Opus) to OpenAI model names. These values are passed through as-is in every request to the target API, so they must exactly match a model name accepted by your OpenAI or Azure OpenAI deployment.

Claude Code reads these variables on startup. From the Claude Code console, use the `/model` command to select the model tier (Haiku, Sonnet, or Opus). Claude Code will use the corresponding OpenAI model name when issuing requests.

## How It Works

```mermaid
flowchart LR
    CC[Claude Code</br>Anthropic /v1/messages request]
    EP[ccproxy /v1/messages</br>MessagesEndpoint]
    A2O[AnthropicToOpenAI</br>request conversion]
    AZ[OpenAI</br>/v1/responses]
    O2A[OpenAIToAnthropic</br>response conversion]
    RESP[Anthropic-compatible</br>response]

    CC --> EP --> A2O --> AZ --> O2A --> RESP

    subgraph Streaming Path
        SSE[SseReader parses SSE]
        SST[StreamingStateTracker</br>builds Anthropic stream events]
        AZ --> SSE --> SST --> RESP
    end

    RESP --> CC
```

The proxy transparently converts requests to the OpenAI Responses API format and responses back to Anthropic format. The model name from the request is passed through to the target API and echoed back in responses.

## Endpoints

### POST `/v1/messages`

Main proxy endpoint. Accepts Anthropic Messages API requests (both streaming and non-streaming), converts them to OpenAI Responses API format, forwards to the configured endpoint, and converts the response back.

### POST `/shutdown`

Gracefully shuts down the proxy. Returns `{"status":"shutting_down"}`.

## Diagnostics

Each request produces a concise 2-line summary on stderr:

```
[ccproxy] POST /v1/messages - 200
[ccproxy] gpt-5-codex -> 5 tools 2 messages
```

For full verbose logging (JSON payloads and SSE events), use `--logfile` to write diagnostics to a file:

```bash
dotnet run --project src/ccproxy -- \
  --endpoint https://api.openai.com/v1/responses \
  --key your-api-key \
  --logfile debug.log
```

## API Conversion Reference

### Request Mapping (Anthropic → OpenAI)

| Anthropic Field        | OpenAI Responses API Field |
|------------------------|----------------------------|
| `model`                | `model` (pass-through)     |
| `system`               | `instructions`             |
| `messages`             | `input`                    |
| `max_tokens`           | `max_output_tokens`        |
| `temperature`, `top_p` | Pass through               |
| `tools`                | `tools` (function type)    |
| `tool_choice`          | `tool_choice`              |
| `stream`               | `stream`                   |

### Response Mapping (OpenAI → Anthropic)

| OpenAI Field                | Anthropic Field              |
|-----------------------------|------------------------------|
| `output[].message`          | `content[].text`             |
| `output[].function_call`    | `content[].tool_use`         |
| `status: "completed"`       | `stop_reason: "end_turn"`    |
| `status: "incomplete"`      | `stop_reason: "max_tokens"`  |
| Any function_call in output | `stop_reason: "tool_use"`    |
| `usage`                     | `usage` (direct mapping)     |

## Project Structure

```
ccproxy/
├── ccproxy.sln                         # Solution file
├── src/ccproxy/
│   ├── Program.cs                      # App bootstrap and DI wiring
│   ├── ccproxy.csproj                  # ASP.NET Core proxy project
│   ├── appsettings.json                # Default ASP.NET logging config
│   ├── Configuration/
│   │   └── ProxyConfig.cs              # CLI/env config parsing and validation
│   ├── Endpoints/
│   │   ├── MessagesEndpoint.cs         # /v1/messages handler
│   │   └── ShutdownEndpoint.cs         # /shutdown handler
│   ├── Conversion/
│   │   ├── AnthropicToOpenAI.cs        # Anthropic -> OpenAI request mapping
│   │   ├── OpenAIToAnthropic.cs        # OpenAI -> Anthropic response mapping
│   │   └── StreamingStateTracker.cs    # Streaming event state machine
│   ├── Proxy/
│   │   ├── OpenAIProxy.cs              # Responses API client
│   │   └── SseReader.cs                # SSE stream parser
│   ├── Diagnostics/
│   │   └── Logger.cs                   # Stderr summaries + optional file logging
│   └── Properties/
│       └── launchSettings.json         # Local launch profile
├── tests/ccproxy.Tests/
│   ├── ccproxy.Tests.csproj            # xUnit test project
│   ├── GlobalUsings.cs                 # Shared test usings
│   ├── EndToEndTests.cs                # End-to-end endpoint and streaming tests
    └── Conversion/
        ├── AnthropicToOpenAITests.cs
        ├── OpenAIToAnthropicTests.cs
        └── StreamingStateTrackerTests.cs
```

## Testing

```bash
# Run all tests
dotnet test

# Manual non-streaming test
curl -X POST http://localhost:5186/v1/messages \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-5-codex","max_tokens":100,"messages":[{"role":"user","content":"Say hello"}]}'

# Manual streaming test
curl -X POST http://localhost:5186/v1/messages --no-buffer \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-5-codex","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"Say hello"}]}'
```

## License

See [LICENSE](LICENSE).
