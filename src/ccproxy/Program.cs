using CCProxy.Configuration;
using CCProxy.Diagnostics;
using CCProxy.Endpoints;
using CCProxy.Proxy;

ProxyConfig config = ProxyConfig.FromArgs(args);
config.Validate();

Logger.Initialize(config.LogFile);

WebApplicationBuilder builder = WebApplication.CreateBuilder();

// Disable default URL configuration - we control the port
builder.WebHost.UseUrls($"http://localhost:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<OpenAIProxy>();
builder.Services.AddSingleton<HttpClient>();

WebApplication app = builder.Build();

// Map endpoints
MessagesEndpoint.Map(app);
ShutdownEndpoint.Map(app);

// Startup banner
Logger.LogInfo($"CCProxy started on http://localhost:{config.Port}");
Logger.LogInfo($"Target endpoint: {config.EndpointUrl}");
Logger.LogInfo($"Target model: {config.Model}");
if (config.LogFile != null)
    Logger.LogInfo($"Log file: {config.LogFile}");

app.Run();

Logger.Shutdown();

// Make Program accessible for WebApplicationFactory in tests
public partial class Program { }
