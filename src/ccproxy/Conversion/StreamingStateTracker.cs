using System.Text.Json.Nodes;
using CCProxy.Proxy;

namespace CCProxy.Conversion;

/// <summary>
/// Stateful converter that translates a stream of OpenAI SSE events into Anthropic SSE events.
/// Tracks content block indices and maps output items to their corresponding Anthropic block positions.
/// One instance should be created per streaming request.
/// </summary>
public class StreamingStateTracker
{
    private readonly string messageId = $"msg_{Guid.NewGuid():N}";
    private readonly string requestedModel;
    private bool hasToolUse = false;

    /// <summary>
    /// Creates a new tracker for a single streaming response.
    /// </summary>
    /// <param name="requestedModel">Original model name from the Anthropic request, echoed back in <c>message_start</c>.</param>
    public StreamingStateTracker(string requestedModel)
    {
        this.requestedModel = requestedModel;
    }

    /// <summary>
    /// Gets the number of tool invocations seen so far in this streaming response.
    /// </summary>
    public int ToolUseCount { get; private set; }

    /// <summary>
    /// Processes a single OpenAI SSE event and yields zero or more corresponding Anthropic SSE events.
    /// </summary>
    public IEnumerable<(string EventType, JsonObject Data)> ProcessEvent(SseEvent sseEvent)
    {
        string eventType = sseEvent.EventType;
        JsonNode? data;

        try
        {
            data = JsonNode.Parse(sseEvent.Data);
        }
        catch
        {
            yield break;
        }

        if (data == null)
        {
            yield break;
        }

        switch (eventType)
        {
            case "response.created":
                yield return ("message_start", this.CreateMessageStart());
                break;

            case "response.output_item.added":
                foreach ((string, JsonObject) e in this.HandleOutputItemAdded(data))
                    yield return e;
                break;

            case "response.output_item.done":
                foreach ((string, JsonObject) e in this.HandleOutputItemDone(data))
                    yield return e;
                break;

            case "response.content_part.added":
                foreach ((string, JsonObject) e in this.HandleContentPartAdded(data))
                    yield return e;
                break;

            case "response.output_text.delta":
                yield return ("content_block_delta", this.CreateTextDelta(data));
                break;

            case "response.function_call_arguments.delta":
                yield return ("content_block_delta", this.CreateFunctionCallInputDelta(data));
                break;

            case "response.content_part.done":
                foreach ((string, JsonObject) e in this.HandleContentPartDone(data))
                    yield return e;
                break;

            case "response.completed":
                foreach ((string, JsonObject) e in this.HandleCompleted(data))
                    yield return e;
                break;

            default:
                Console.WriteLine($"========== {eventType} ==========");
                break;
        }
    }

    /// <summary>
    /// Creates the initial <c>message_start</c> event with empty content and zero usage counters.
    /// </summary>
    private JsonObject CreateMessageStart()
    {
        return new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = this.messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = this.requestedModel,
                ["content"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 0,
                    ["output_tokens"] = 0,
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = 0,
                }
            }
        };
    }

    /// <summary>
    /// Handles <c>response.output_item.added</c> — emits a <c>content_block_start</c> for function calls
    /// and tracks the mapping from output item ID to Anthropic block index.
    /// </summary>
    private IEnumerable<(string, JsonObject)> HandleOutputItemAdded(JsonNode data)
    {
        int blockIndex = data["output_index"]?.GetValue<int>() ?? 0;
        JsonNode? item = data["item"];
        if (item == null)
        {
            yield break;
        }

        string? type = item["type"]?.GetValue<string>();
        string itemId = item["id"]?.GetValue<string>() ?? "";

        if (type == "function_call")
        {
            this.hasToolUse = true;

            yield return ("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = blockIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(""),
                    ["name"] = item["name"]?.DeepClone() ?? JsonValue.Create(""),
                    ["input"] = new JsonObject()
                }
            });
        }
        else if (type == "reasoning")
        {
            yield return ("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = blockIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "thinking",
                    ["signature"] = item["id"]?.GetValue<string>() ?? "",
                    ["thinking"] = ""
                }
            });
        }
    }

    /// <summary>
    /// Handles <c>response.content_part.added</c> — emits a <c>content_block_start</c> for text parts
    /// and tracks the mapping from (output_index, content_index) to Anthropic block index.
    /// </summary>
    private IEnumerable<(string, JsonObject)> HandleContentPartAdded(JsonNode data)
    {
        JsonNode? part = data["part"];

        if (part == null)
        {
            yield break;
        }

        string? type = part["type"]?.GetValue<string>();
        if (type == "output_text")
        {
            int blockIndex = data["output_index"]?.GetValue<int>() ?? 0;
            int outputIndex = data["output_index"]?.GetValue<int>() ?? 0;
            int contentIndex = data["content_index"]?.GetValue<int>() ?? 0;

            yield return ("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = blockIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ""
                }
            });
        }
    }

    /// <summary>
    /// Creates a <c>content_block_delta</c> with a <c>text_delta</c> payload from an <c>response.output_text.delta</c> event.
    /// </summary>
    private JsonObject CreateTextDelta(JsonNode data)
    {
        int blockIndex = data["output_index"]?.GetValue<int>() ?? 0;
        //int blockIndex = this.FindTextBlockIndex(data);

        return new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = blockIndex,
            ["delta"] = new JsonObject
            {
                ["type"] = "text_delta",
                ["text"] = data["delta"]?.DeepClone() ?? JsonValue.Create("")
            }
        };
    }

    /// <summary>
    /// Creates a <c>content_block_delta</c> with an <c>input_json_delta</c> payload from a
    /// <c>response.function_call_arguments.delta</c> event.
    /// </summary>
    private JsonObject CreateFunctionCallInputDelta(JsonNode data)
    {
        string itemId = data["item_id"]?.GetValue<string>() ?? "";
        string delta = data["delta"]?.DeepClone().GetValue<string>() ?? "";
        int blockIndex = data["output_index"]?.GetValue<int>() ?? 0;
        
        return new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = blockIndex,
            ["delta"] = new JsonObject
            {
                ["type"] = "input_json_delta",
                ["partial_json"] = delta
            }
        };
    }

    /// <summary>
    /// Handles <c>response.content_part.done</c> — emits a <c>content_block_stop</c> for the corresponding text block.
    /// </summary>
    private IEnumerable<(string, JsonObject)> HandleContentPartDone(JsonNode data)
    {
        int blockIndex = data["output_index"]?.GetValue<int>() ?? 0;
        yield return ("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = blockIndex
        });
    }

    /// <summary>
    /// Handles <c>response.output_item.done</c> — emits a <c>content_block_stop</c> for completed function calls.
    /// </summary>
    private IEnumerable<(string, JsonObject)> HandleOutputItemDone(JsonNode data)
    {
        yield return ("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = data["output_index"]?.GetValue<int>() ?? 0
        });

        JsonNode? item = data["item"];
        if (item == null)
        {
            yield break;
        }

        string itemType = item["type"]?.GetValue<string>() ?? "";
        string status = item["status"]?.GetValue<string>() ?? "";

        if (itemType == "function_call" && status == "completed")
        {
            this.ToolUseCount++;

            JsonNode? functionArgs = null;
            JsonNode? argumentsNode = item["arguments"];

            if (argumentsNode is JsonValue value &&
                value.TryGetValue<string>(out string? jsonText) &&
                !string.IsNullOrWhiteSpace(jsonText))
            {
                functionArgs = JsonNode.Parse(jsonText);
            }
            else
            {
                functionArgs = argumentsNode?.DeepClone();
            }

            yield return ("tool_use", new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = item["call_id"]?.GetValue<string>(),
                ["name"] = item["name"]?.GetValue<string>() ?? "",
                ["input"] = functionArgs
            });
        }
    }

    /// <summary>
    /// Handles <c>response.completed</c> — emits <c>message_delta</c> (with stop reason and usage)
    /// followed by <c>message_stop</c> to close the stream.
    /// </summary>
    private IEnumerable<(string, JsonObject)> HandleCompleted(JsonNode data)
    {
        JsonNode? response = data["response"];
        string stopReason = this.hasToolUse ? "tool_use" : "end_turn";
        if (response?["status"]?.GetValue<string>() == "incomplete")
        {
            stopReason = "max_tokens";
        }

        JsonNode inputTokens = response?["usage"]?["input_tokens"]?.DeepClone() ?? 0;
        JsonNode outputTokens = response?["usage"]?["output_tokens"]?.DeepClone() ?? 0;

        yield return ("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null
            },
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens
            }
        });

        yield return ("message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
    }
}
