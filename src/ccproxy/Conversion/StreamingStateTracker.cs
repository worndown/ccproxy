using System.Text.Json.Nodes;
using CCProxy.Configuration;
using CCProxy.Proxy;

namespace CCProxy.Conversion;

/// <summary>
/// Stateful converter that translates a stream of OpenAI SSE events into Anthropic SSE events.
/// Tracks content block indices and maps output items to their corresponding Anthropic block positions.
/// One instance should be created per streaming request.
/// </summary>
public class StreamingStateTracker
{
    private readonly string _messageId = $"msg_{Guid.NewGuid():N}";
    private readonly ProxyConfig _config;
    private readonly string? _requestedModel;
    private readonly Dictionary<string, int> _outputItemToBlockIndex = new();
    private readonly Dictionary<(int OutputIndex, int ContentIndex), int> _contentPartToBlockIndex = new();
    private int _contentBlockIndex = 0;
    private bool _hasToolUse = false;
    

    /// <summary>
    /// Creates a new tracker for a single streaming response.
    /// </summary>
    /// <param name="config">Proxy configuration.</param>
    /// <param name="requestedModel">Original model name from the Anthropic request, echoed back in <c>message_start</c>.</param>
    public StreamingStateTracker(ProxyConfig config, string? requestedModel = null)
    {
        _config = config;
        _requestedModel = requestedModel;
    }

    /// <summary>
    /// Gets the number of tool invocations seen so far in this streaming response.
    /// Incremented by <see cref="HandleOutputItemAdded"/> each time a <c>response.output_item.added</c>
    /// event with <c>type == "function_call"</c> adds an entry to <see cref="_outputItemToBlockIndex"/>.
    /// Read after the stream loop completes to get the final count.
    /// </summary>
    public int ToolUseCount => _outputItemToBlockIndex.Count;

    /// <summary>
    /// Processes a single OpenAI SSE event and yields zero or more corresponding Anthropic SSE events.
    /// </summary>
    public IEnumerable<(string EventType, JsonObject Data)> ProcessEvent(SseEvent sseEvent)
    {
        var eventType = sseEvent.EventType;
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(sseEvent.Data);
        }
        catch
        {
            yield break;
        }

        if (data == null) yield break;

        switch (eventType)
        {
            case "response.created":
                yield return ("message_start", CreateMessageStart());
                break;

            case "response.output_item.added":
                foreach (var e in HandleOutputItemAdded(data))
                    yield return e;
                break;

            case "response.content_part.added":
                foreach (var e in HandleContentPartAdded(data))
                    yield return e;
                break;

            case "response.output_text.delta":
                yield return ("content_block_delta", CreateTextDelta(data));
                break;

            case "response.function_call_arguments.delta":
                yield return ("content_block_delta", CreateInputJsonDelta(data));
                break;

            case "response.content_part.done":
                foreach (var e in HandleContentPartDone(data))
                    yield return e;
                break;

            case "response.output_item.done":
                foreach (var e in HandleOutputItemDone(data))
                    yield return e;
                break;

            case "response.completed":
                foreach (var e in HandleCompleted(data))
                    yield return e;
                break;
        }
    }

    private JsonObject CreateMessageStart()
    {
        return new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = _messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = _requestedModel ?? _config.Model,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
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

    private IEnumerable<(string, JsonObject)> HandleOutputItemAdded(JsonNode data)
    {
        var item = data["item"];
        if (item == null) yield break;

        var type = item["type"]?.GetValue<string>();
        var itemId = item["id"]?.GetValue<string>() ?? "";

        if (type == "function_call")
        {
            _hasToolUse = true;
            var index = _contentBlockIndex++;
            _outputItemToBlockIndex[itemId] = index;

            yield return ("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = index,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(""),
                    ["name"] = item["name"]?.DeepClone() ?? JsonValue.Create(""),
                    ["input"] = new JsonObject()
                }
            });
        }
    }

    private IEnumerable<(string, JsonObject)> HandleContentPartAdded(JsonNode data)
    {
        var part = data["part"];
        if (part == null) yield break;

        var type = part["type"]?.GetValue<string>();
        if (type == "output_text")
        {
            var index = _contentBlockIndex++;
            var outputIndex = data["output_index"]?.GetValue<int>() ?? 0;
            var contentIndex = data["content_index"]?.GetValue<int>() ?? 0;
            _contentPartToBlockIndex[(outputIndex, contentIndex)] = index;

            yield return ("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = index,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ""
                }
            });
        }
    }

    private JsonObject CreateTextDelta(JsonNode data)
    {
        var blockIndex = FindTextBlockIndex(data);

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

    private JsonObject CreateInputJsonDelta(JsonNode data)
    {
        var itemId = data["item_id"]?.GetValue<string>() ?? "";
        var blockIndex = _outputItemToBlockIndex.GetValueOrDefault(itemId, 0);

        return new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = blockIndex,
            ["delta"] = new JsonObject
            {
                ["type"] = "input_json_delta",
                ["partial_json"] = data["delta"]?.DeepClone() ?? JsonValue.Create("")
            }
        };
    }

    private IEnumerable<(string, JsonObject)> HandleContentPartDone(JsonNode data)
    {
        var blockIndex = FindTextBlockIndex(data);
        yield return ("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = blockIndex
        });
    }

    private IEnumerable<(string, JsonObject)> HandleOutputItemDone(JsonNode data)
    {
        var item = data["item"];
        if (item == null) yield break;

        var type = item["type"]?.GetValue<string>();
        if (type == "function_call")
        {
            var itemId = item["id"]?.GetValue<string>() ?? "";
            var blockIndex = _outputItemToBlockIndex.GetValueOrDefault(itemId, 0);

            yield return ("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = blockIndex
            });
        }
    }

    private IEnumerable<(string, JsonObject)> HandleCompleted(JsonNode data)
    {
        var response = data["response"];
        var stopReason = _hasToolUse ? "tool_use" : "end_turn";
        if (response?["status"]?.GetValue<string>() == "incomplete")
            stopReason = "max_tokens";

        var inputTokens = response?["usage"]?["input_tokens"]?.DeepClone() ?? 0;
        var outputTokens = response?["usage"]?["output_tokens"]?.DeepClone() ?? 0;

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
                ["output_tokens"] = outputTokens
            }
        });

        yield return ("message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
    }

    private int FindTextBlockIndex(JsonNode data)
    {
        var outputIndex = data["output_index"]?.GetValue<int>() ?? 0;
        var contentIndex = data["content_index"]?.GetValue<int>() ?? 0;
        var key = (outputIndex, contentIndex);

        if (_contentPartToBlockIndex.TryGetValue(key, out var blockIndex))
            return blockIndex;

        return contentIndex; // fallback for safety
    }
}
