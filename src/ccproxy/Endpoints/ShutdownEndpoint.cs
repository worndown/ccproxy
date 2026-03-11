using System.Text.Json.Nodes;

namespace CCProxy.Endpoints;

/// <summary>
/// Handles the <c>POST /shutdown</c> endpoint for graceful proxy termination.
/// </summary>
public static class ShutdownEndpoint
{
    /// <summary>Maps the <c>/shutdown</c> route to the application.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/shutdown", async (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            context.Response.ContentType = "application/json";
            var response = new JsonObject { ["status"] = "shutting_down" };
            await context.Response.WriteAsync(response.ToJsonString());
        });
    }
}
