using System.Text.Json.Nodes;

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
    /// <param name="requestedModel">The model name from the original request, echoed back in the response.</param>
    /// <returns>An Anthropic Messages API response as a <see cref="JsonObject"/>.</returns>
    public static JsonObject Convert(JsonNode openAiResponse, string requestedModel)
    {
        JsonArray content = new JsonArray();
        bool hasToolUse = false;

        if (openAiResponse["output"] is JsonArray outputArray)
        {
            foreach (JsonNode? item in outputArray)
            {
                if (item == null)
                {
                    continue;
                }

                string? type = item["type"]?.GetValue<string>();

                if (type == "message")
                {
                    // Extract text content from message output
                    if (item["content"] is JsonArray msgContent)
                    {
                        foreach (JsonNode? part in msgContent)
                        {
                            if (part?["type"]?.GetValue<string>() == "output_text")
                            {
                                content.Add((JsonNode)new JsonObject
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

                    content.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(""),
                        ["name"] = item["name"]?.DeepClone() ?? JsonValue.Create(""),
                        ["input"] = parsedInput
                    });
                }
            }
        }

        string stopReason = hasToolUse ? "tool_use" : "end_turn";
        string? status = openAiResponse["status"]?.GetValue<string>();
        if (status == "incomplete")
        {
            stopReason = "max_tokens";
        }

        string responseId = $"msg_{Guid.NewGuid():N}";

        JsonObject usage = new JsonObject
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
            ["model"] = requestedModel,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = usage,
        };
    }
}
