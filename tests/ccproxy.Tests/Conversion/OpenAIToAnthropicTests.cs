using System.Text.Json.Nodes;
using CCProxy.Conversion;

namespace CCProxy.Tests.Conversion;

public class OpenAIToAnthropicTests
{

    [Fact]
    public void Convert_TextResponse_MapsCorrectly()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_123",
            "status": "completed",
            "output": [
                {
                    "type": "message",
                    "content": [
                        {"type": "output_text", "text": "Hello there!"}
                    ]
                }
            ],
            "usage": {"input_tokens": 10, "output_tokens": 5}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "gpt-5-codex");

        Assert.StartsWith("msg_", result["id"]!.GetValue<string>());
        Assert.Equal("message", result["type"]!.GetValue<string>());
        Assert.Equal("assistant", result["role"]!.GetValue<string>());
        Assert.Equal("gpt-5-codex", result["model"]!.GetValue<string>());
        Assert.Equal("end_turn", result["stop_reason"]!.GetValue<string>());

        var content = result["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("Hello there!", content[0]!["text"]!.GetValue<string>());

        Assert.Equal(10, result["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(5, result["usage"]!["output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Convert_FunctionCallResponse_MapsToToolUse()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_456",
            "status": "completed",
            "output": [
                {
                    "type": "function_call",
                    "call_id": "call_abc",
                    "name": "get_weather",
                    "arguments": "{\"location\":\"NYC\"}"
                }
            ],
            "usage": {"input_tokens": 20, "output_tokens": 15}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "test-model");

        Assert.Equal("tool_use", result["stop_reason"]!.GetValue<string>());
        var content = result["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("tool_use", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("call_abc", content[0]!["id"]!.GetValue<string>());
        Assert.Equal("get_weather", content[0]!["name"]!.GetValue<string>());
        Assert.Equal("NYC", content[0]!["input"]!["location"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_MixedResponse_HasTextAndToolUse()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_789",
            "status": "completed",
            "output": [
                {
                    "type": "message",
                    "content": [
                        {"type": "output_text", "text": "Let me check."}
                    ]
                },
                {
                    "type": "function_call",
                    "call_id": "call_def",
                    "name": "search",
                    "arguments": "{\"query\":\"test\"}"
                }
            ],
            "usage": {"input_tokens": 30, "output_tokens": 25}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "test-model");

        Assert.Equal("tool_use", result["stop_reason"]!.GetValue<string>());
        var content = result["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", content[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_IncompleteStatus_MapsToMaxTokens()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_inc",
            "status": "incomplete",
            "output": [
                {
                    "type": "message",
                    "content": [
                        {"type": "output_text", "text": "Partial..."}
                    ]
                }
            ],
            "usage": {"input_tokens": 10, "output_tokens": 100}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "test-model");
        Assert.Equal("max_tokens", result["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_InvalidArguments_DefaultsToEmptyObject()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_bad",
            "status": "completed",
            "output": [
                {
                    "type": "function_call",
                    "call_id": "call_bad",
                    "name": "test",
                    "arguments": "not valid json"
                }
            ],
            "usage": {"input_tokens": 5, "output_tokens": 3}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "test-model");
        var content = result["content"]!.AsArray();
        Assert.NotNull(content[0]!["input"]);
    }

    [Fact]
    public void Convert_HasCacheTokenFields()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_c",
            "status": "completed",
            "output": [{"type": "message", "content": [{"type": "output_text", "text": "Hi"}]}],
            "usage": {"input_tokens": 10, "output_tokens": 5}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "test-model");
        Assert.Equal(0, result["usage"]!["cache_creation_input_tokens"]!.GetValue<int>());
        Assert.Equal(0, result["usage"]!["cache_read_input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Convert_ReturnsRequestedModel()
    {
        var response = JsonNode.Parse("""
        {
            "id": "resp_m",
            "status": "completed",
            "output": [{"type": "message", "content": [{"type": "output_text", "text": "Hi"}]}],
            "usage": {"input_tokens": 10, "output_tokens": 5}
        }
        """)!;

        var result = OpenAIToAnthropic.Convert(response, "claude-sonnet-4-20250514");
        Assert.Equal("claude-sonnet-4-20250514", result["model"]!.GetValue<string>());
    }
}
