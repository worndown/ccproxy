namespace CCProxy.Endpoints;

/// <summary>
/// Handles the <c>POST /shutdown</c> endpoint for graceful proxy termination.
/// </summary>
public static class ShutdownEndpoint
{
    /// <summary>Maps the <c>/shutdown</c> route to the application.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Json(new { status = "shutting_down" });
        });
    }
}
