using System.Text.Json.Nodes;

namespace CCProxy.Conversion;

/// <summary>
/// Converts Anthropic Messages API requests to OpenAI Responses API format.
/// Handles messages, system prompts, tools, tool_choice, and image content.
/// </summary>
public static class AnthropicToOpenAI
{
    /// <summary>
    /// Converts an Anthropic <c>/v1/messages</c> request body into an OpenAI Responses API request body.
    /// The model name from the Anthropic request is passed through as-is.
    /// </summary>
    /// <param name="anthropicRequest">The incoming Anthropic request JSON.</param>
    /// <param name="stream">Whether to set <c>"stream": true</c> on the outbound request.</param>
    /// <returns>An OpenAI Responses API request as a <see cref="JsonObject"/>.</returns>
    public static JsonObject Convert(JsonNode anthropicRequest, bool stream = false)
    {
        JsonObject result = new JsonObject
        {
            ["model"] = anthropicRequest["model"]?.DeepClone(),
        };

        // reasoning -> high
        // Anthropic uses adaptive and thinking budgets (expressed in tokens).
        // This doesn't convert to OpenAI models, so we're always using high reasoning effort.
        result["reasoning"] = new JsonObject
        {
            ["effort"] = "high"
        };

        // max_tokens -> max_output_tokens
        if (anthropicRequest["max_tokens"] is { } maxTokens)
        {
            // Claude Code can set max tokens to 1 when sending first, test message.
            // OpenAI requires max_output_tokens to be >= 16; otherwise request will fail.
            int tokenValue = maxTokens.GetValue<int>();
            result["max_output_tokens"] = Math.Max(tokenValue, 16);
        }

        // temperature, top_p pass through
        if (anthropicRequest["temperature"] is { } temp)
        {
            result["temperature"] = temp.DeepClone();
        }

        if (anthropicRequest["top_p"] is { } topP)
        {
            result["top_p"] = topP.DeepClone();
        }

        // system -> instructions
        if (anthropicRequest["system"] is { } system)
        {
            result["instructions"] = ConvertSystem(system);
        }

        // messages -> input
        result["input"] = ConvertMessages(anthropicRequest["messages"]?.AsArray());

        // tools
        if (anthropicRequest["tools"] is JsonArray { Count: > 0 } tools)
        {
            result["tools"] = ConvertTools(tools);
        }

        // tool_choice
        if (anthropicRequest["tool_choice"] is { } toolChoice)
        {
            result["tool_choice"] = ConvertToolChoice(toolChoice);
        }

        if (stream)
        {
            result["stream"] = true;
        }

        return result;
    }

    private static string ConvertSystem(JsonNode system)
    {
        if (system is JsonArray arr)
        {
            List<string> parts = new List<string>();
            foreach (JsonNode? item in arr)
            {
                if (item?["text"] is { } text)
                {
                    parts.Add(text.GetValue<string>());
                }
                else if (item is JsonValue val)
                {
                    parts.Add(val.GetValue<string>());
                }
            }
            return string.Join("\n", parts);
        }

        return system.GetValue<string>();
    }

    private static JsonArray ConvertMessages(JsonArray? messages)
    {
        JsonArray input = new JsonArray();

        if (messages == null)
        {
            return input;
        }

        foreach (JsonNode? msg in messages)
        {
            if (msg == null)
            {
                continue;
            }

            string? role = msg["role"]?.GetValue<string>();
            JsonNode? content = msg["content"];

            if (role == "user")
            {
                ConvertUserMessage(content, input);
            }
            else if (role == "assistant")
            {
                ConvertAssistantMessage(content, input);
            }
        }

        return input;
    }

    private static void ConvertUserMessage(JsonNode? content, JsonArray input)
    {
        if (content is JsonValue textVal)
        {
            // Simple string content
            input.Add((JsonNode)new JsonObject
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
            JsonArray messageContent = new JsonArray();
            foreach (JsonNode? block in contentArray)
            {
                if (block == null)
                {
                    continue;
                }

                string? type = block["type"]?.GetValue<string>();

                if (type == "tool_result")
                {
                    // tool_result -> function_call_output (top-level input item)
                    input.Add((JsonNode)ConvertToolResult(block));
                }
                else if (type == "text")
                {
                    messageContent.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = block["text"]!.DeepClone()
                    });
                }
                else if (type == "image")
                {
                    JsonNode? source = block["source"];
                    string mediaType = source?["media_type"]?.GetValue<string>() ?? "image/png";
                    string data = source?["data"]?.GetValue<string>() ?? "";
                    messageContent.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:{mediaType};base64,{data}"
                    });
                }
            }

            if (messageContent.Count > 0)
            {
                input.Add((JsonNode)new JsonObject
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
        string toolUseId = block["tool_use_id"]?.GetValue<string>() ?? "";
        string output = ExtractToolResultContent(block["content"]);

        JsonObject result = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = toolUseId,
            ["output"] = output
        };

        return result;
    }

    private static string ExtractToolResultContent(JsonNode? content)
    {
        if (content == null)
        {
            return "";
        }

        if (content is JsonValue val)
        {
            return val.GetValue<string>();
        }

        if (content is JsonArray arr)
        {
            List<string> parts = new List<string>();
            foreach (var item in arr)
            {
                if (item?["type"]?.GetValue<string>() == "text")
                {
                    parts.Add(item["text"]?.GetValue<string>() ?? "");
                }
                else if (item?["type"]?.GetValue<string>() == "image")
                {
                    // For images in tool results, serialize as JSON
                    parts.Add(item.ToJsonString());
                }
                else
                {
                    parts.Add(item?.ToJsonString() ?? "");
                }
            }

            return string.Join("\n", parts);
        }

        return content.ToJsonString();
    }

    private static void ConvertAssistantMessage(JsonNode? content, JsonArray input)
    {
        if (content is JsonValue textVal)
        {
            input.Add((JsonNode)new JsonObject
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
            List<string> textParts = new List<string>();

            foreach (JsonNode? block in contentArray)
            {
                if (block == null)
                {
                    continue;
                }

                string? type = block["type"]?.GetValue<string>();

                if (type == "text")
                {
                    textParts.Add(block["text"]?.GetValue<string>() ?? "");
                }
                else if (type == "tool_use")
                {
                    // Flush accumulated text first
                    if (textParts.Count > 0)
                    {
                        input.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["content"] = string.Join("", textParts)
                        });

                        textParts.Clear();
                    }

                    string arguments = block["input"] is { } inp ? inp.ToJsonString() : "{}";

                    input.Add((JsonNode)new JsonObject
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
                input.Add((JsonNode)new JsonObject
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
        JsonArray result = new JsonArray();
        foreach (JsonNode? tool in tools)
        {
            if (tool == null) continue;
            result.Add((JsonNode)new JsonObject
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
        string? type = toolChoice["type"]?.GetValue<string>();

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
