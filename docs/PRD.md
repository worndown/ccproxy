# Claude Code local proxy - Product Requirement Document 

Product requirements for the 'ccproxy' application.

## 1. Overview
- The goal of the application is to allow Claude Code to use OpenAI models hosted in Azure.
- The application is running on local machine and exposing Claude compatible '/v1/messages' REST endpoint that Claude Code can use.
- After receiving request, the application forwards it to the target model. Depending on the target model request might need to be transformed to satisfy the schema of the target model.
- When response is received, it is transformed to match '/v1/messages' response schema and returned to the caller.

## 2. Functional Requirements

### 2.1 Application Type
- The application should be written in C# using .NET 10.
- The application should be ASP.NET console app.

### 2.2 Command Line Arguments
The application can receive necessary operational parameters as command line arguments or use user's environment variables, if defined. If both are present, cmdline arguments take precedence over environment variables.
The following arguments are supported:

| Cmdline Argument | Environment Variable | Description |
|------------------|:--------------------:|-------------|
| port | CCPROXY_PORT | Local port application listens on (default: 5186).  |
| endpoint | CCPROXY_ENDPOINT_URL | Azure endpoint url for the model deployment |
| model | CCPROXY_MODEL | Target Azure hosted model |
| key | CCPROXY_API_KEY | Azure API key |
| verbosity | - | Controls diagnostic output verbosity |

### 2.3 Endpoints
The application should be exposing two REST endpoints. Endpoints require no authentication and only accepting requests from the localhost.

#### 2.3.1 "/v1/messages" endpoint
- This is a POST operation.
- Serves as the main contract between ccproxy and Claude Code.
- All input parameters are passed in the message body and documented in [Anthropic documentation](https://platform.claude.com/docs/en/api/messages/create).
- The same document describes return message and streaming events.

#### 2.3.2 "/shutdown" endpoint
- POST operation
- When invoked, the application gracefully shuts itself down.

### 2.4 Target model
- The target model is Azure hosted GPT-5-Codex.
- Use [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create) to communicate with this model.

## 3. Implementation details

- **Streaming**: Support both streaming (SSE) and non-streaming responses.
- **Implementation approach**: Direct JSON manipulation with `System.Text.Json` (no Azure.AI.OpenAI SDK).
- **Tool use**: Full tool/function calling conversion between Anthropic and OpenAI formats.
- **Authentication**: API key only (no Azure AD).
- **Model mapping**: Always use configured target model, ignore incoming model name from Claude Code.
- **Verbosity**: Simple on/off flag (0=quiet, 1=verbose with full JSON dumps).

## 4. Diagnostics
Add minimal and configurable diagnostics, that could be toggled on or off and controlled by the **verbosity** parameter. We want to be able to dump request/response json messages and other information necessary for troubleshooting.

## 5. Testing and Validation
I deployed GPT-5-Codex model in Azure and you can use it during implementation for validation and testing. Just read user environment variables defined above: CCPROXY_PORT, CCPROXY_ENDPOINT_URL, CCPROXY_MODEL and CCPROXY_API_KEY.

