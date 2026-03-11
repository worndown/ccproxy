using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CCProxy.Proxy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CCProxy.Tests;

public class EndToEndTests : IClassFixture<EndToEndTests.TestFactory>
{
    private readonly HttpClient client;

    public EndToEndTests(TestFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task NonStreaming_TextResponse_ReturnsAnthropicFormat()
    {
        var request = new StringContent("""
        {
            "model": "claude-sonnet-4-20250514",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Say hello"}]
        }
        """, Encoding.UTF8, "application/json");

        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("message", body!["type"]!.GetValue<string>());
        Assert.Equal("assistant", body["role"]!.GetValue<string>());
        Assert.StartsWith("msg_", body["id"]!.GetValue<string>());
        Assert.Equal("end_turn", body["stop_reason"]!.GetValue<string>());

        var content = body["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("Mocked response", content[0]!["text"]!.GetValue<string>());

        // Model in response should echo back the requested model, not the target model
        Assert.Equal("claude-sonnet-4-20250514", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task NonStreaming_ToolUseResponse_ReturnsToolUse()
    {
        var request = new StringContent("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": [{"role": "user", "content": "Get weather"}],
            "tools": [{"name": "get_weather", "description": "Get weather", "input_schema": {"type": "object"}}]
        }
        """, Encoding.UTF8, "application/json");

        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("message", body!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streaming_ReturnsSSEEvents()
    {
        var request = new StringContent("""
        {
            "model": "x",
            "max_tokens": 100,
            "stream": true,
            "messages": [{"role": "user", "content": "Say hello"}]
        }
        """, Encoding.UTF8, "application/json");

        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message_start", body);
        Assert.Contains("event: message_stop", body);
    }

    [Fact]
    public async Task MissingMaxTokens_Returns400()
    {
        var request = new StringContent("""
        {
            "model": "x",
            "messages": [{"role": "user", "content": "Hi"}]
        }
        """, Encoding.UTF8, "application/json");

        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("error", body!["type"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", body["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyMessages_Returns400()
    {
        var request = new StringContent("""
        {
            "model": "x",
            "max_tokens": 100,
            "messages": []
        }
        """, Encoding.UTF8, "application/json");

        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WrongContentType_Returns415()
    {
        var request = new StringContent("not json", Encoding.UTF8, "text/plain");
        var response = await this.client.PostAsync("/v1/messages", request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Shutdown_ReturnsShuttingDown()
    {
        // Use a separate factory to avoid shutting down the shared one
        using var factory = new TestFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/shutdown", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("shutting_down", body!["status"]!.GetValue<string>());
    }

    public class TestFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // Set required env vars for config validation
            Environment.SetEnvironmentVariable("CCPROXY_ENDPOINT_URL", "https://mock.openai.azure.com");
            Environment.SetEnvironmentVariable("CCPROXY_API_KEY", "test-key");

            builder.ConfigureServices(services =>
            {
                // Replace OpenAIProxy with a mock
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(OpenAIProxy));
                if (descriptor != null)
                    services.Remove(descriptor);

                // Replace HttpClient with mock handler
                var httpDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(HttpClient));
                if (httpDescriptor != null)
                    services.Remove(httpDescriptor);

                services.AddSingleton(new HttpClient(new MockOpenAIHandler()));
                services.AddSingleton<OpenAIProxy>();
            });
        }
    }

    private class MockOpenAIHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestBody = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
            var requestJson = JsonNode.Parse(requestBody);
            var isStreaming = requestJson?["stream"]?.GetValue<bool>() ?? false;

            if (isStreaming)
            {
                return Task.FromResult(CreateStreamingResponse());
            }

            return Task.FromResult(CreateNonStreamingResponse());
        }

        private static HttpResponseMessage CreateNonStreamingResponse()
        {
            var responseBody = """
            {
                "id": "resp_mock",
                "status": "completed",
                "output": [
                    {
                        "type": "message",
                        "content": [
                            {"type": "output_text", "text": "Mocked response"}
                        ]
                    }
                ],
                "usage": {"input_tokens": 10, "output_tokens": 5}
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage CreateStreamingResponse()
        {
            var sseData = new StringBuilder();
            sseData.AppendLine("event: response.created");
            sseData.AppendLine("""data: {"id":"resp_mock","status":"in_progress"}""");
            sseData.AppendLine();

            sseData.AppendLine("event: response.content_part.added");
            sseData.AppendLine("""data: {"part":{"type":"output_text","text":""},"output_index":0,"content_index":0}""");
            sseData.AppendLine();

            sseData.AppendLine("event: response.output_text.delta");
            sseData.AppendLine("""data: {"delta":"Hello","output_index":0,"content_index":0}""");
            sseData.AppendLine();

            sseData.AppendLine("event: response.content_part.done");
            sseData.AppendLine("""data: {"output_index":0,"content_index":0}""");
            sseData.AppendLine();

            sseData.AppendLine("event: response.completed");
            sseData.AppendLine("""data: {"response":{"status":"completed","usage":{"input_tokens":10,"output_tokens":3}}}""");
            sseData.AppendLine();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseData.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
