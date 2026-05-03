using CCProxy.Conversion;
using CCProxy.Proxy;
using Xunit;

namespace CCProxy.Tests.Conversion;

public class StreamingStateTrackerTests
{

    [Fact]
    public void ProcessEvent_ResponseCreated_EmitsMessageStart()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");
        var events = tracker.ProcessEvent(new SseEvent("response.created", """
        {
            "id": "resp_1",
            "status": "in_progress"
        }
        """)).ToList();

        Assert.Single(events);
        Assert.Equal("message_start", events[0].EventType);
        var msg = events[0].Data["message"]!;
        Assert.Equal("assistant", msg["role"]!.GetValue<string>());
        Assert.Equal("gpt-5-codex", msg["model"]!.GetValue<string>());
    }

    [Fact]
    public void ProcessEvent_TextStreaming_EmitsCorrectSequence()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        // content_part.added (output_text)
        var addedEvents = tracker.ProcessEvent(new SseEvent("response.content_part.added", """
        {
            "part": {"type": "output_text", "text": ""},
            "output_index": 0,
            "content_index": 0
        }
        """)).ToList();

        Assert.Single(addedEvents);
        Assert.Equal("content_block_start", addedEvents[0].EventType);
        Assert.Equal("text", addedEvents[0].Data["content_block"]!["type"]!.GetValue<string>());

        // text delta
        var deltaEvents = tracker.ProcessEvent(new SseEvent("response.output_text.delta", """
        {
            "delta": "Hello ",
            "output_index": 0,
            "content_index": 0
        }
        """)).ToList();

        Assert.Single(deltaEvents);
        Assert.Equal("content_block_delta", deltaEvents[0].EventType);
        Assert.Equal("text_delta", deltaEvents[0].Data["delta"]!["type"]!.GetValue<string>());
        Assert.Equal("Hello ", deltaEvents[0].Data["delta"]!["text"]!.GetValue<string>());

        // content_part.done
        var doneEvents = tracker.ProcessEvent(new SseEvent("response.content_part.done", """
        {
            "output_index": 0,
            "content_index": 0
        }
        """)).ToList();

        Assert.Single(doneEvents);
        Assert.Equal("content_block_stop", doneEvents[0].EventType);
    }

    [Fact]
    public void ProcessEvent_FunctionCallStreaming_EmitsToolUseBlocks()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        // output_item.added (function_call)
        var addedEvents = tracker.ProcessEvent(new SseEvent("response.output_item.added", """
        {
            "item": {
                "type": "function_call",
                "id": "fc_1",
                "call_id": "call_abc",
                "name": "get_weather"
            },
            "output_index": 0
        }
        """)).ToList();

        Assert.Single(addedEvents);
        Assert.Equal("content_block_start", addedEvents[0].EventType);
        var block = addedEvents[0].Data["content_block"]!;
        Assert.Equal("tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("call_abc", block["id"]!.GetValue<string>());
        Assert.Equal("get_weather", block["name"]!.GetValue<string>());

        // arguments delta
        var argEvents = tracker.ProcessEvent(new SseEvent("response.function_call_arguments.delta", """
        {
            "delta": "{\"location\":",
            "item_id": "fc_1",
            "output_index": 0
        }
        """)).ToList();

        Assert.Single(argEvents);
        Assert.Equal("content_block_delta", argEvents[0].EventType);
        Assert.Equal("input_json_delta", argEvents[0].Data["delta"]!["type"]!.GetValue<string>());

        // output_item.done
        var doneEvents = tracker.ProcessEvent(new SseEvent("response.output_item.done", """
        {
            "item": {
                "type": "function_call",
                "id": "fc_1",
                "call_id": "call_abc",
                "name": "get_weather",
                "arguments": "{\"location\":\"NYC\"}"
            },
            "output_index": 0
        }
        """)).ToList();

        Assert.Single(doneEvents);
        Assert.Equal("content_block_stop", doneEvents[0].EventType);
    }

    [Fact]
    public void ProcessEvent_Completed_EmitsMessageDeltaAndStop()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        var events = tracker.ProcessEvent(new SseEvent("response.completed", """
        {
            "response": {
                "status": "completed",
                "usage": {"input_tokens": 100, "output_tokens": 50}
            }
        }
        """)).ToList();

        Assert.Equal(2, events.Count);
        Assert.Equal("message_delta", events[0].EventType);
        Assert.Equal("end_turn", events[0].Data["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal(50, events[0].Data["usage"]!["output_tokens"]!.GetValue<int>());

        Assert.Equal("message_stop", events[1].EventType);
    }

    [Fact]
    public void ProcessEvent_CompletedWithToolUse_StopReasonIsToolUse()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        // First add a function call to set the hasToolUse flag
        tracker.ProcessEvent(new SseEvent("response.output_item.added", """
        {
            "item": {"type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "test"},
            "output_index": 0
        }
        """)).ToList();

        var events = tracker.ProcessEvent(new SseEvent("response.completed", """
        {
            "response": {
                "status": "completed",
                "usage": {"input_tokens": 100, "output_tokens": 50}
            }
        }
        """)).ToList();

        Assert.Equal("tool_use", events[0].Data["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ProcessEvent_IncompleteStatus_StopReasonIsMaxTokens()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        var events = tracker.ProcessEvent(new SseEvent("response.completed", """
        {
            "response": {
                "status": "incomplete",
                "usage": {"input_tokens": 100, "output_tokens": 4096}
            }
        }
        """)).ToList();

        Assert.Equal("max_tokens", events[0].Data["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ProcessEvent_TextThenToolThenText_AssignsDistinctBlockIndices()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");

        // First text block at output_index=0
        var text1Events = tracker.ProcessEvent(new SseEvent("response.content_part.added", """
        {
            "part": {"type": "output_text", "text": ""},
            "output_index": 0,
            "content_index": 0
        }
        """)).ToList();
        Assert.Equal(0, text1Events[0].Data["index"]!.GetValue<int>());

        // Function call at output_index=1
        var toolEvents = tracker.ProcessEvent(new SseEvent("response.output_item.added", """
        {
            "item": {"type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "read_file"},
            "output_index": 1
        }
        """)).ToList();
        Assert.Equal(1, toolEvents[0].Data["index"]!.GetValue<int>());

        // Second text block at output_index=2, content_index=0 (same content_index as first!)
        var text2Events = tracker.ProcessEvent(new SseEvent("response.content_part.added", """
        {
            "part": {"type": "output_text", "text": ""},
            "output_index": 2,
            "content_index": 0
        }
        """)).ToList();
        Assert.Equal(2, text2Events[0].Data["index"]!.GetValue<int>());

        // Delta for second text block should route to block index 2, not 0
        var deltaEvents = tracker.ProcessEvent(new SseEvent("response.output_text.delta", """
        {
            "delta": "world",
            "output_index": 2,
            "content_index": 0
        }
        """)).ToList();
        Assert.Equal(2, deltaEvents[0].Data["index"]!.GetValue<int>());
    }

    [Fact]
    public void ProcessEvent_UnknownEvent_ReturnsEmpty()
    {
        var tracker = new StreamingStateTracker("gpt-5-codex");
        var events = tracker.ProcessEvent(new SseEvent("response.unknown", "{}")).ToList();
        Assert.Empty(events);
    }
}
