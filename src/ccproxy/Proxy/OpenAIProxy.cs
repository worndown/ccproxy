using System.Text;
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
    private readonly HttpClient httpClient;
    private readonly ProxyConfig config;

    public OpenAIProxy(HttpClient httpClient, ProxyConfig config)
    {
        this.httpClient = httpClient;
        this.config = config;
        this.httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    private string BuildRequestUrl()
    {
        string baseUrl = this.config.EndpointUrl.TrimEnd('/');
        return $"{baseUrl}/openai/responses?api-version=2025-03-01-preview";
    }

    /// <summary>
    /// Sends a non-streaming request to the Azure OpenAI endpoint and returns the parsed JSON response.
    /// </summary>
    /// <exception cref="OpenAIProxyException">Thrown when Azure returns a non-success status code.</exception>
    public async Task<JsonNode?> SendRequestAsync(JsonObject requestBody, CancellationToken cancellationToken = default)
    {
        string url = this.BuildRequestUrl();
        Logger.LogRequest("OpenAI Request", requestBody);

        StringContent content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", this.config.ApiKey);

        HttpResponseMessage response = await this.httpClient.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenAIProxyException((int)response.StatusCode, responseBody);
        }

        JsonNode? result = JsonNode.Parse(responseBody);
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
        string url = this.BuildRequestUrl();
        Logger.LogRequest("OpenAI Streaming Request", requestBody);

        StringContent content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", this.config.ApiKey);

        HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OpenAIProxyException((int)response.StatusCode, errorBody);
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
        this.StatusCode = statusCode;
        this.ResponseBody = responseBody;
    }
}
