# CCProxy

A local proxy that enables Claude Code to use Azure-hosted OpenAI models. CCProxy translates between the Anthropic `/v1/messages` API and OpenAI's Responses API (`/v1/responses`), so Claude Code works seamlessly with models like GPT-5-Codex deployed on Azure.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An Azure OpenAI deployment with a Responses API-compatible model

## Configuration

CCProxy accepts configuration via command-line arguments or environment variables. CLI arguments take precedence.

| CLI Argument   | Environment Variable   | Description                                  | Default |
|----------------|------------------------|----------------------------------------------|---------|
| `--port`       | `CCPROXY_PORT`         | Local port to listen on                      | `5186`  |
| `--endpoint`   | `CCPROXY_ENDPOINT_URL` | Azure endpoint base URL                      | —       |
| `--model`      | `CCPROXY_MODEL`        | Target Azure model deployment name           | —       |
| `--key`        | `CCPROXY_API_KEY`      | Azure API key                                | —       |
| `--verbosity`  | —                      | `0` = quiet, `1` = verbose JSON logging      | `0`     |

## Quick Start

```bash
# Using CLI arguments
dotnet run --project src/ccproxy -- \
  --endpoint https://your-resource.openai.azure.com \
  --model gpt-5-codex \
  --key your-api-key

# Or using environment variables
export CCPROXY_ENDPOINT_URL=https://your-resource.openai.azure.com
export CCPROXY_MODEL=gpt-5-codex
export CCPROXY_API_KEY=your-api-key
dotnet run --project src/ccproxy
```

## Using with Claude Code

Point Claude Code at the proxy by setting the base URL:

```bash
export ANTHROPIC_BASE_URL=http://localhost:5186
```

Then use Claude Code as usual. The proxy transparently converts requests to the OpenAI Responses API format and responses back to Anthropic format. The model name from Claude Code is echoed back in responses so Claude Code remains unaware of the underlying model.

## Endpoints

### POST `/v1/messages`

Main proxy endpoint. Accepts Anthropic Messages API requests (both streaming and non-streaming), converts them to OpenAI Responses API format, forwards to Azure, and converts the response back.

### POST `/shutdown`

Gracefully shuts down the proxy. Returns `{"status":"shutting_down"}`.

## Diagnostics

Set `--verbosity 1` to enable verbose logging. Request and response JSON payloads are logged to stderr, keeping stdout clean. This is useful for troubleshooting conversion issues.

```bash
dotnet run --project src/ccproxy -- \
  --endpoint https://your-resource.openai.azure.com \
  --model gpt-5-codex \
  --key your-api-key \
  --verbosity 1
```

## API Conversion Reference

### Request Mapping (Anthropic → OpenAI)

| Anthropic Field        | OpenAI Responses API Field |
|------------------------|----------------------------|
| `model`                | Configured `--model` value |
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
├── src/ccproxy/
│   ├── Program.cs                  # Entry point and DI wiring
│   ├── Configuration/
│   │   └── ProxyConfig.cs          # CLI args and env var parsing
│   ├── Endpoints/
│   │   ├── MessagesEndpoint.cs     # /v1/messages handler
│   │   └── ShutdownEndpoint.cs     # /shutdown handler
│   ├── Conversion/
│   │   ├── AnthropicToOpenAI.cs    # Request translation
│   │   ├── OpenAIToAnthropic.cs    # Response translation
│   │   └── StreamingStateTracker.cs # SSE event state machine
│   ├── Proxy/
│   │   ├── OpenAIProxy.cs          # HTTP client for Azure
│   │   └── SseReader.cs            # SSE protocol parser
│   └── Diagnostics/
│       └── Logger.cs               # Verbose logging to stderr
├── tests/ccproxy.Tests/
│   ├── Conversion/
│   │   ├── AnthropicToOpenAITests.cs
│   │   ├── OpenAIToAnthropicTests.cs
│   │   └── StreamingStateTrackerTests.cs
│   └── EndToEndTests.cs
└── docs/
    └── PRD.md
```

## Testing

```bash
# Run all tests
dotnet test

# Manual non-streaming test
curl -X POST http://localhost:5186/v1/messages \
  -H "Content-Type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","max_tokens":100,"messages":[{"role":"user","content":"Say hello"}]}'

# Manual streaming test
curl -X POST http://localhost:5186/v1/messages --no-buffer \
  -H "Content-Type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"Say hello"}]}'
```

## License

See [LICENSE](LICENSE).
