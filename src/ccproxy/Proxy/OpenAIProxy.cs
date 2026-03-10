using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCProxy.Configuration;
using CCProxy.Diagnostics;

namespace CCProxy.Proxy;

/// <summary>
/// HTTP client for forwarding requests to the Azure OpenAI Responses API.
/// Constructs the full API URL from the configured endpoint and handles authentication via <c>api-key</c> header.
/// </summary>
public class OpenAIProxy
{
    private readonly HttpClient _httpClient;
    private readonly ProxyConfig _config;

    public OpenAIProxy(HttpClient httpClient, ProxyConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    private string BuildRequestUrl()
    {
        var baseUrl = _config.EndpointUrl.TrimEnd('/');
        return $"{baseUrl}/openai/responses?api-version=2025-03-01-preview";
    }

    /// <summary>
    /// Sends a non-streaming request to the Azure OpenAI endpoint and returns the parsed JSON response.
    /// </summary>
    /// <exception cref="OpenAIProxyException">Thrown when Azure returns a non-success status code.</exception>
    public async Task<JsonNode?> SendRequestAsync(JsonObject requestBody, CancellationToken cancellationToken = default)
    {
        var url = BuildRequestUrl();
        Logger.LogRequest("OpenAI Request", requestBody);

        var content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", _config.ApiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenAIProxyException((int)response.StatusCode, responseBody);
        }

        var result = JsonNode.Parse(responseBody);
        Logger.LogResponse("OpenAI Response", result);
        return result;
    }

    /// <summary>
    /// Sends a streaming request to the Azure OpenAI endpoint.
    /// Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the response body can be
    /// consumed as an SSE stream.
    /// </summary>
    /// <returns>The response body stream and HTTP status code.</returns>
    /// <exception cref="OpenAIProxyException">Thrown when Azure returns a non-success status code.</exception>
    public async Task<(Stream Stream, int StatusCode)> SendStreamingRequestAsync(JsonObject requestBody, CancellationToken cancellationToken = default)
    {
        var url = BuildRequestUrl();
        Logger.LogRequest("OpenAI Streaming Request", requestBody);

        var content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", _config.ApiKey);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OpenAIProxyException((int)response.StatusCode, errorBody);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (stream, (int)response.StatusCode);
    }
}

/// <summary>
/// Thrown when the Azure OpenAI API returns a non-success HTTP status code.
/// Contains the status code and raw response body for error mapping.
/// </summary>
public class OpenAIProxyException : Exception
{
    /// <summary>The HTTP status code returned by Azure.</summary>
    public int StatusCode { get; }

    /// <summary>The raw response body from Azure.</summary>
    public string ResponseBody { get; }

    public OpenAIProxyException(int statusCode, string responseBody)
        : base($"OpenAI API returned {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
