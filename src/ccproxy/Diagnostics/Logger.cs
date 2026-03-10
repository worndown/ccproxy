using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCProxy.Diagnostics;

/// <summary>
/// Simple diagnostic logger that writes to stderr to keep stdout clean.
/// Controlled by the <see cref="Verbose"/> flag — when off, only <see cref="LogInfo"/>
/// and <see cref="LogError"/> produce output.
/// </summary>
public static class Logger
{
    private static readonly JsonSerializerOptions s_prettyOptions = new() { WriteIndented = true };

    /// <summary>When <c>true</c>, request/response JSON payloads and SSE events are logged.</summary>
    public static bool Verbose { get; set; }

    /// <summary>Logs an informational message (always printed).</summary>
    public static void LogInfo(string message)
    {
        Console.Error.WriteLine($"[ccproxy] {message}");
    }

    /// <summary>Logs an error message (always printed).</summary>
    public static void LogError(string message)
    {
        Console.Error.WriteLine($"[ccproxy] ERROR: {message}");
    }

    /// <summary>Logs a request JSON payload when verbose mode is enabled.</summary>
    public static void LogRequest(string label, JsonNode? json)
    {
        if (!Verbose) return;
        Console.Error.WriteLine($"[ccproxy] >>> {label}:");
        Console.Error.WriteLine(json?.ToJsonString(s_prettyOptions) ?? "(null)");
    }

    /// <summary>Logs a response JSON payload when verbose mode is enabled.</summary>
    public static void LogResponse(string label, JsonNode? json)
    {
        if (!Verbose) return;
        Console.Error.WriteLine($"[ccproxy] <<< {label}:");
        Console.Error.WriteLine(json?.ToJsonString(s_prettyOptions) ?? "(null)");
    }

    /// <summary>Logs an SSE event (direction, type, and data) when verbose mode is enabled.</summary>
    public static void LogSseEvent(string direction, string eventType, string data)
    {
        if (!Verbose) return;
        Console.Error.WriteLine($"[ccproxy] {direction} SSE event: {eventType}");
        try
        {
            var node = JsonNode.Parse(data);
            Console.Error.WriteLine(node?.ToJsonString(s_prettyOptions) ?? data);
        }
        catch
        {
            Console.Error.WriteLine(data);
        }
    }
}
