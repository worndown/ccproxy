using System.Text.Json.Nodes;
using CCProxy.Configuration;

namespace CCProxy.Conversion;

/// <summary>
/// Converts Anthropic Messages API requests to OpenAI Responses API format.
/// Handles messages, system prompts, tools, tool_choice, and image content.
/// </summary>
public static class AnthropicToOpenAI
{
    /// <summary>
    /// Converts an Anthropic <c>/v1/messages</c> request body into an OpenAI Responses API request body.
    /// The outbound model is always set from <paramref name="config"/>, ignoring the incoming model name.
    /// </summary>
    /// <param name="anthropicRequest">The incoming Anthropic request JSON.</param>
    /// <param name="config">Proxy configuration (provides the target model and endpoint).</param>
    /// <param name="stream">Whether to set <c>"stream": true</c> on the outbound request.</param>
    /// <returns>An OpenAI Responses API request as a <see cref="JsonObject"/>.</returns>
    public static JsonObject Convert(JsonNode anthropicRequest, ProxyConfig config, bool stream = false)
    {
        var result = new JsonObject
        {
            ["model"] = config.Model,
        };

        // max_tokens -> max_output_tokens
        if (anthropicRequest["max_tokens"] is JsonNode maxTokens)
            result["max_output_tokens"] = maxTokens.DeepClone();

        // temperature, top_p pass through
        if (anthropicRequest["temperature"] is JsonNode temp)
            result["temperature"] = temp.DeepClone();
        if (anthropicRequest["top_p"] is JsonNode topP)
            result["top_p"] = topP.DeepClone();

        // system -> instructions
        if (anthropicRequest["system"] is JsonNode system)
            result["instructions"] = ConvertSystem(system);

        // messages -> input
        result["input"] = ConvertMessages(anthropicRequest["messages"]?.AsArray());

        // tools
        if (anthropicRequest["tools"] is JsonArray tools && tools.Count > 0)
            result["tools"] = ConvertTools(tools);

        // tool_choice
        if (anthropicRequest["tool_choice"] is JsonNode toolChoice)
            result["tool_choice"] = ConvertToolChoice(toolChoice);

        if (stream)
            result["stream"] = true;

        return result;
    }

    private static string ConvertSystem(JsonNode system)
    {
        if (system is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr)
            {
                if (item?["text"] is JsonNode text)
                    parts.Add(text.GetValue<string>());
                else if (item is JsonValue val)
                    parts.Add(val.GetValue<string>());
            }
            return string.Join("\n", parts);
        }
        return system.GetValue<string>();
    }

    private static JsonArray ConvertMessages(JsonArray? messages)
    {
        var input = new JsonArray();
        if (messages == null) return input;

        foreach (var msg in messages)
        {
            if (msg == null) continue;
            var role = msg["role"]?.GetValue<string>();
            var content = msg["content"];

            if (role == "user")
                ConvertUserMessage(content, input);
            else if (role == "assistant")
                ConvertAssistantMessage(content, input);
        }

        return input;
    }

    private static void ConvertUserMessage(JsonNode? content, JsonArray input)
    {
        if (content is JsonValue textVal)
        {
            // Simple string content
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = textVal.DeepClone()
            });
            return;
        }

        if (content is JsonArray contentArray)
        {
            // Process content blocks - separate tool_results from text/image content
            var messageContent = new JsonArray();
            foreach (var block in contentArray)
            {
                if (block == null) continue;
                var type = block["type"]?.GetValue<string>();

                if (type == "tool_result")
                {
                    // tool_result -> function_call_output (top-level input item)
                    input.Add(ConvertToolResult(block));
                }
                else if (type == "text")
                {
                    messageContent.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = block["text"]!.DeepClone()
                    });
                }
                else if (type == "image")
                {
                    var source = block["source"];
                    var mediaType = source?["media_type"]?.GetValue<string>() ?? "image/png";
                    var data = source?["data"]?.GetValue<string>() ?? "";
                    messageContent.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:{mediaType};base64,{data}"
                    });
                }
            }

            if (messageContent.Count > 0)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = messageContent
                });
            }
        }
    }

    private static JsonObject ConvertToolResult(JsonNode block)
    {
        var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? "";
        var output = ExtractToolResultContent(block["content"]);

        var result = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = toolUseId,
            ["output"] = output
        };

        return result;
    }

    private static string ExtractToolResultContent(JsonNode? content)
    {
        if (content == null) return "";
        if (content is JsonValue val) return val.GetValue<string>();
        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr)
            {
                if (item?["type"]?.GetValue<string>() == "text")
                    parts.Add(item["text"]?.GetValue<string>() ?? "");
                else if (item?["type"]?.GetValue<string>() == "image")
                {
                    // For images in tool results, serialize as JSON
                    parts.Add(item.ToJsonString());
                }
                else
                    parts.Add(item?.ToJsonString() ?? "");
            }
            return string.Join("\n", parts);
        }
        return content.ToJsonString();
    }

    private static void ConvertAssistantMessage(JsonNode? content, JsonArray input)
    {
        if (content is JsonValue textVal)
        {
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = textVal.DeepClone()
            });
            return;
        }

        if (content is JsonArray contentArray)
        {
            // Collect text blocks for a single message, emit tool_use as separate items
            var textParts = new List<string>();

            foreach (var block in contentArray)
            {
                if (block == null) continue;
                var type = block["type"]?.GetValue<string>();

                if (type == "text")
                {
                    textParts.Add(block["text"]?.GetValue<string>() ?? "");
                }
                else if (type == "tool_use")
                {
                    // Flush accumulated text first
                    if (textParts.Count > 0)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["content"] = string.Join("", textParts)
                        });
                        textParts.Clear();
                    }

                    var arguments = block["input"] is JsonNode inp
                        ? inp.ToJsonString()
                        : "{}";
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = block["id"]?.DeepClone() ?? JsonValue.Create(""),
                        ["name"] = block["name"]?.DeepClone() ?? JsonValue.Create(""),
                        ["arguments"] = arguments
                    });
                }
            }

            // Flush remaining text
            if (textParts.Count > 0)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = string.Join("", textParts)
                });
            }
        }
    }

    private static JsonArray ConvertTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool == null) continue;
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool["name"]?.DeepClone() ?? JsonValue.Create(""),
                ["description"] = tool["description"]?.DeepClone(),
                ["parameters"] = tool["input_schema"]?.DeepClone()
            });
        }
        return result;
    }

    private static JsonNode ConvertToolChoice(JsonNode toolChoice)
    {
        var type = toolChoice["type"]?.GetValue<string>();
        return type switch
        {
            "auto" => JsonValue.Create("auto"),
            "any" => JsonValue.Create("required"),
            "tool" => new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolChoice["name"]?.DeepClone()
            },
            _ => JsonValue.Create("auto")
        };
    }
}
