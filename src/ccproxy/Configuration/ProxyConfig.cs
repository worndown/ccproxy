namespace CCProxy.Configuration;

/// <summary>
/// Holds proxy configuration parsed from CLI arguments and environment variables.
/// </summary>
public class ProxyConfig
{
    /// <summary>Local port the proxy listens on.</summary>
    public int Port { get; set; } = 5186;

    /// <summary>Azure OpenAI endpoint base URL (e.g. <c>https://{resource}.openai.azure.com</c>).</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>Azure API key used for authentication.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional log file path for verbose JSON diagnostics. Null = no file logging.</summary>
    public string? LogFile { get; set; }

    /// <summary>
    /// Creates a <see cref="ProxyConfig"/> from command-line arguments and environment variables.
    /// CLI arguments (<c>--port</c>, <c>--endpoint</c>, <c>--key</c>, <c>--logfile</c>)
    /// take precedence over their corresponding <c>CCPROXY_*</c> environment variables.
    /// </summary>
    public static ProxyConfig FromArgs(string[] args)
    {
        var config = new ProxyConfig();

        // Load from environment variables first
        if (Environment.GetEnvironmentVariable("CCPROXY_PORT") is { } portEnv && int.TryParse(portEnv, out var envPort))
        {
            config.Port = envPort;
        }

        if (Environment.GetEnvironmentVariable("CCPROXY_ENDPOINT_URL") is { } endpointEnv &&
            !string.IsNullOrEmpty(endpointEnv))
        {
            config.EndpointUrl = endpointEnv;
        }

        if (Environment.GetEnvironmentVariable("CCPROXY_API_KEY") is { } keyEnv && !string.IsNullOrEmpty(keyEnv))
        {
            config.ApiKey = keyEnv;
        }

        // CLI args override env vars
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (int.TryParse(args[++i], out var port))
                        config.Port = port;
                    break;
                case "--endpoint":
                    config.EndpointUrl = args[++i];
                    break;
                case "--key":
                    config.ApiKey = args[++i];
                    break;
                case "--logfile":
                    config.LogFile = args[++i];
                    break;
            }
        }

        return config;
    }

    /// <summary>
    /// Validates that all required configuration fields are set.
    /// Throws <see cref="InvalidOperationException"/> listing all missing fields.
    /// </summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(EndpointUrl))
        {
            errors.Add("Endpoint URL is required (--endpoint or CCPROXY_ENDPOINT_URL)");
        }

        if (string.IsNullOrEmpty(ApiKey))
        {
            errors.Add("API key is required (--key or CCPROXY_API_KEY)");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Configuration errors:\n" + string.Join("\n", errors.Select(e => $"  - {e}")));
        }
    }
}
