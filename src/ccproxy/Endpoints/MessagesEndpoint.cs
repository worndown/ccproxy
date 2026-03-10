using System.Text.Json.Nodes;
using CCProxy.Configuration;
using CCProxy.Conversion;
using CCProxy.Diagnostics;
using CCProxy.Proxy;

namespace CCProxy.Endpoints;

/// <summary>
/// Handles the <c>POST /v1/messages</c> endpoint — the main proxy entry point.
/// Accepts Anthropic Messages API requests, converts them to OpenAI Responses API format,
/// forwards to Azure, and converts the response back. Supports both streaming (SSE) and
/// non-streaming modes.
/// </summary>
public static class MessagesEndpoint
{
    /// <summary>Maps the <c>/v1/messages</c> route to the application.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/messages", async (HttpContext context, OpenAIProxy proxy, ProxyConfig config) =>
        {
            // Content-Type validation
            if (!context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                context.Response.StatusCode = 415;
                await context.Response.WriteAsJsonAsync(CreateError("invalid_request_error", "Content-Type must be application/json"));
                return;
            }

            JsonNode? requestBody;
            try
            {
                requestBody = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(CreateError("invalid_request_error", "Invalid JSON in request body"));
                return;
            }

            if (requestBody == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(CreateError("invalid_request_error", "Empty request body"));
                return;
            }

            Logger.LogRequest("Anthropic Request", requestBody);

            // Validate required fields
            int maxTokens = requestBody["max_tokens"]?.GetValue<int>() ?? 0;
            if (maxTokens <= 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(CreateError("invalid_request_error", "max_tokens must be greater than 0"));
                return;
            }

            JsonArray? messages = requestBody["messages"]?.AsArray();
            if (messages == null || messages.Count == 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(CreateError("invalid_request_error", "messages must be a non-empty array"));
                return;
            }

            bool isStreaming = requestBody["stream"]?.GetValue<bool>() ?? false;
            string? requestedModel = requestBody["model"]?.GetValue<string>();
            int toolCount = requestBody["tools"]?.AsArray()?.Count ?? 0;
            int messageCount = messages.Count;
            int summaryStatusCode = 200;
            int toolUseCount = 0;

            try
            {
                if (isStreaming)
                {
                    toolUseCount = await HandleStreaming(context, requestBody, proxy, config, requestedModel);
                }
                else
                {
                    toolUseCount = await HandleNonStreaming(context, requestBody, proxy, config, requestedModel);
                }
            }
            catch (OpenAIProxyException ex)
            {
                Logger.LogError($"Azure API error: {ex.StatusCode} - {ex.ResponseBody}");
                (string anthropicErrorType, int statusCode) = MapErrorStatus(ex.StatusCode);
                summaryStatusCode = statusCode;

                if (isStreaming && context.Response.HasStarted)
                {
                    // Mid-stream error
                    await WriteSseEvent(context, "error", new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = anthropicErrorType,
                            ["message"] = ex.ResponseBody
                        }
                    });
                    await WriteSseEvent(context, "message_stop", new JsonObject { ["type"] = "message_stop" });
                    return;
                }

                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(CreateError(anthropicErrorType, ex.ResponseBody));
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError($"Network error: {ex.Message}");
                summaryStatusCode = 502;
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(CreateError("api_error", $"Failed to connect to Azure endpoint: {ex.Message}"));
            }
            catch (TaskCanceledException)
            {
                Logger.LogError("Request timed out");
                summaryStatusCode = 504;
                context.Response.StatusCode = 504;
                await context.Response.WriteAsJsonAsync(CreateError("api_error", "Request to Azure endpoint timed out"));
            }
            finally
            {
                Logger.LogRequestSummary("POST", "/v1/messages", summaryStatusCode, config.Model, toolCount, toolUseCount, messageCount);
            }
        });
    }

    private static async Task<int> HandleNonStreaming(HttpContext context, JsonNode requestBody, OpenAIProxy proxy, ProxyConfig config, string? requestedModel)
    {
        JsonObject openAiRequest = AnthropicToOpenAI.Convert(requestBody, config, stream: false);
        JsonNode? openAiResponse = await proxy.SendRequestAsync(openAiRequest, context.RequestAborted);

        if (openAiResponse == null)
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(CreateError("api_error", "Empty response from Azure endpoint"));
            return 0;
        }

        JsonObject anthropicResponse = OpenAIToAnthropic.Convert(openAiResponse, config, requestedModel);
        Logger.LogResponse("Anthropic Response", anthropicResponse);
        await context.Response.WriteAsJsonAsync(anthropicResponse);

        int? toolUseCount = anthropicResponse["content"]?.AsArray()
            .Count(item => item?["type"]?.GetValue<string>() == "tool_use");
        return toolUseCount ?? 0;
    }

    private static async Task<int> HandleStreaming(HttpContext context, JsonNode requestBody, OpenAIProxy proxy, ProxyConfig config, string? requestedModel)
    {
        JsonObject openAiRequest = AnthropicToOpenAI.Convert(requestBody, config, stream: true);
        (Stream stream, _) = await proxy.SendStreamingRequestAsync(openAiRequest, context.RequestAborted);

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        StreamingStateTracker tracker = new StreamingStateTracker(config, requestedModel);

        await using (stream)
        {
            await foreach (SseEvent sseEvent in SseReader.ReadEventsAsync(stream, context.RequestAborted))
            {
                Logger.LogSseEvent("<<<", sseEvent.EventType, sseEvent.Data);

                foreach ((string eventType, JsonObject data) in tracker.ProcessEvent(sseEvent))
                {
                    await WriteSseEvent(context, eventType, data);
                }
            }
        }

        // ToolUseCount is populated during the stream loop above: each "response.output_item.added"
        // event with type "function_call" increments the count inside StreamingStateTracker.
        return tracker.ToolUseCount;
    }

    private static async Task WriteSseEvent(HttpContext context, string eventType, JsonObject data)
    {
        string line = $"event: {eventType}\ndata: {data.ToJsonString()}\n\n";
        Logger.LogSseEvent(">>>", eventType, data.ToJsonString());
        await context.Response.WriteAsync(line, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static (string ErrorType, int StatusCode) MapErrorStatus(int azureStatusCode)
    {
        return azureStatusCode switch
        {
            400 => ("invalid_request_error", 400),
            401 or 403 => ("authentication_error", 401),
            429 => ("rate_limit_error", 429),
            >= 500 => ("overloaded_error", 529),
            _ => ("api_error", 500)
        };
    }

    private static object CreateError(string type, string message)
    {
        return new { type = "error", error = new { type, message } };
    }
}
