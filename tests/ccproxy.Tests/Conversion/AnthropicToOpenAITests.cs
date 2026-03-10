using System.Text.Json.Nodes;
using CCProxy.Configuration;
using CCProxy.Conversion;

namespace CCProxy.Tests.Conversion;

public class AnthropicToOpenAITests
{
    private readonly ProxyConfig config = new()
    {
        Model = "gpt-5-codex",
        EndpointUrl = "https://test.openai.azure.com",
        ApiKey = "test-key"
    };

    [Fact]
    public void Convert_SimpleTextMessage_MapsCorrectly()
    {
        var request = JsonNode.Parse("""
        {
            "model": "claude-sonnet-4-20250514",
            "max_tokens": 1024,
            "messages": [
                {"role": "user", "content": "Hello"}
            ]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);

        Assert.Equal("gpt-5-codex", result["model"]!.GetValue<string>());
        Assert.Equal(1024, result["max_output_tokens"]!.GetValue<int>());

        var input = result["input"]!.AsArray();
        Assert.Single(input);
        Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("Hello", input[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_IgnoresIncomingModel()
    {
        var request = JsonNode.Parse("""
        {
            "model": "claude-opus-4-20250514",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal("gpt-5-codex", result["model"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_SystemString_MapsToInstructions()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "system": "You are helpful",
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal("You are helpful", result["instructions"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_SystemArray_ConcatenatesText()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "system": [{"type": "text", "text": "Part 1"}, {"type": "text", "text": "Part 2"}],
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal("Part 1\nPart 2", result["instructions"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_TemperatureAndTopP_PassThrough()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "temperature": 0.7,
            "top_p": 0.9,
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal(0.7, result["temperature"]!.GetValue<double>(), 0.001);
        Assert.Equal(0.9, result["top_p"]!.GetValue<double>(), 0.001);
    }

    [Fact]
    public void Convert_MultiTurnConversation_MapsCorrectly()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [
                {"role": "user", "content": "Hello"},
                {"role": "assistant", "content": "Hi there!"},
                {"role": "user", "content": "How are you?"}
            ]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var input = result["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", input[1]!["role"]!.GetValue<string>());
        Assert.Equal("user", input[2]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_Tools_MapsToFunctionType()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}],
            "tools": [{
                "name": "get_weather",
                "description": "Gets the weather",
                "input_schema": {
                    "type": "object",
                    "properties": {
                        "location": {"type": "string"}
                    },
                    "required": ["location"]
                }
            }]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var tools = result["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("Gets the weather", tools[0]!["description"]!.GetValue<string>());
        Assert.NotNull(tools[0]!["parameters"]);
    }

    [Fact]
    public void Convert_ToolChoiceAuto_MapsToAuto()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}],
            "tool_choice": {"type": "auto"}
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal("auto", result["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_ToolChoiceAny_MapsToRequired()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}],
            "tool_choice": {"type": "any"}
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        Assert.Equal("required", result["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_ToolChoiceTool_MapsToFunctionName()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}],
            "tool_choice": {"type": "tool", "name": "get_weather"}
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var tc = result["tool_choice"]!;
        Assert.Equal("function", tc["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tc["name"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_AssistantToolUse_MapsToFunctionCall()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [
                {"role": "user", "content": "What's the weather?"},
                {"role": "assistant", "content": [
                    {"type": "text", "text": "Let me check."},
                    {"type": "tool_use", "id": "call_123", "name": "get_weather", "input": {"location": "NYC"}}
                ]},
                {"role": "user", "content": [
                    {"type": "tool_result", "tool_use_id": "call_123", "content": "Sunny, 72°F"}
                ]}
            ]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var input = result["input"]!.AsArray();

        // Should have: user message, assistant text, function_call, function_call_output
        Assert.Equal(4, input.Count);

        // Assistant text
        Assert.Equal("message", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("assistant", input[1]!["role"]!.GetValue<string>());
        Assert.Equal("Let me check.", input[1]!["content"]!.GetValue<string>());

        // Function call
        Assert.Equal("function_call", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("call_123", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("get_weather", input[2]!["name"]!.GetValue<string>());

        // Function call output
        Assert.Equal("function_call_output", input[3]!["type"]!.GetValue<string>());
        Assert.Equal("call_123", input[3]!["call_id"]!.GetValue<string>());
        Assert.Equal("Sunny, 72°F", input[3]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_UserImageContent_MapsToInputImage()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{
                "role": "user",
                "content": [
                    {"type": "text", "text": "What's this?"},
                    {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "abc123"}}
                ]
            }]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var input = result["input"]!.AsArray();
        var content = input[0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("input_text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("input_image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,abc123", content[1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_ToolResultArrayContent_StringifiesCorrectly()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{
                "role": "user",
                "content": [
                    {"type": "tool_result", "tool_use_id": "call_1", "content": [
                        {"type": "text", "text": "Line 1"},
                        {"type": "text", "text": "Line 2"}
                    ]}
                ]
            }]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config);
        var input = result["input"]!.AsArray();
        Assert.Equal("function_call_output", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("Line 1\nLine 2", input[0]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_StreamTrue_SetsStreamFlag()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config, stream: true);
        Assert.True(result["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void Convert_StreamFalse_NoStreamField()
    {
        var request = JsonNode.Parse("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """)!;

        var result = AnthropicToOpenAI.Convert(request, this.config, stream: false);
        Assert.Null(result["stream"]);
    }
}
