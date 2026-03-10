using System.Text.Json.Nodes;
using CCProxy.Configuration;

namespace CCProxy.Conversion;

/// <summary>
/// Converts OpenAI Responses API responses back to Anthropic Messages API format.
/// Handles text output, function calls, usage, and stop reason mapping.
/// </summary>
public static class OpenAIToAnthropic
{
    /// <summary>
    /// Converts an OpenAI Responses API response into an Anthropic <c>/v1/messages</c> response.
    /// </summary>
    /// <param name="openAiResponse">The OpenAI response JSON.</param>
    /// <param name="config">Proxy configuration (provides fallback model name).</param>
    /// <param name="requestedModel">
    /// The model name from the original Anthropic request. When provided, this value is echoed
    /// back in the response so Claude Code remains unaware of the underlying model.
    /// Falls back to <c>config.Model</c> if <c>null</c>.
    /// </param>
    /// <returns>An Anthropic Messages API response as a <see cref="JsonObject"/>.</returns>
    public static JsonObject Convert(JsonNode openAiResponse, ProxyConfig config, string? requestedModel = null)
    {
        var content = new JsonArray();
        var hasToolUse = false;

        if (openAiResponse["output"] is JsonArray outputArray)
        {
            foreach (var item in outputArray)
            {
                if (item == null) continue;
                var type = item["type"]?.GetValue<string>();

                if (type == "message")
                {
                    // Extract text content from message output
                    if (item["content"] is JsonArray msgContent)
                    {
                        foreach (var part in msgContent)
                        {
                            if (part?["type"]?.GetValue<string>() == "output_text")
                            {
                                content.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = part["text"]?.DeepClone() ?? JsonValue.Create("")
                                });
                            }
                        }
                    }
                }
                else if (type == "function_call")
                {
                    hasToolUse = true;
                    JsonNode? parsedInput;
                    try
                    {
                        parsedInput = JsonNode.Parse(item["arguments"]?.GetValue<string>() ?? "{}");
                    }
                    catch
                    {
                        parsedInput = new JsonObject();
                    }

                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(""),
                        ["name"] = item["name"]?.DeepClone() ?? JsonValue.Create(""),
                        ["input"] = parsedInput
                    });
                }
            }
        }

        var stopReason = hasToolUse ? "tool_use" : "end_turn";
        var status = openAiResponse["status"]?.GetValue<string>();
        if (status == "incomplete")
            stopReason = "max_tokens";

        var responseId = $"msg_{Guid.NewGuid():N}";

        var usage = new JsonObject
        {
            ["input_tokens"] = openAiResponse["usage"]?["input_tokens"]?.DeepClone() ?? 0,
            ["output_tokens"] = openAiResponse["usage"]?["output_tokens"]?.DeepClone() ?? 0,
            ["cache_creation_input_tokens"] = 0,
            ["cache_read_input_tokens"] = 0,
        };

        return new JsonObject
        {
            ["id"] = responseId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = requestedModel ?? config.Model,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = usage,
        };
    }
}
