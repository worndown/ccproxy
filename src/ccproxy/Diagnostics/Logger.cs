using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCProxy.Diagnostics;

/// <summary>
/// Diagnostic logger. Info and error messages go to stderr. Verbose JSON payloads
/// and SSE events go to an optional log file. A 2-line request summary is always
/// written to stderr after each request completes.
/// </summary>
public static class Logger
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };
    private static StreamWriter? fileWriter;

    /// <summary>
    /// Initializes file-based verbose logging. If <paramref name="logFilePath"/> is non-null,
    /// opens the file for writing (overwrite mode). Call <see cref="Shutdown"/> on exit.
    /// </summary>
    public static void Initialize(string? logFilePath)
    {
        if (logFilePath != null)
        {
            fileWriter = new StreamWriter(logFilePath, append: false) { AutoFlush = true };
        }
    }

    /// <summary>
    /// Flushes and closes the log file writer, if any.
    /// </summary>
    public static void Shutdown()
    {
        fileWriter?.Flush();
        fileWriter?.Dispose();
        fileWriter = null;
    }

    /// <summary>
    /// Logs an informational message to stderr (always printed).
    /// </summary>
    public static void LogInfo(string message)
    {
        Console.Error.WriteLine($"[ccproxy] {message}");
    }

    /// <summary>
    /// Logs an error message to stderr (always printed).
    /// </summary>
    public static void LogError(string message)
    {
        Console.Error.WriteLine($"[ccproxy] ERROR: {message}");
    }

    /// <summary>
    /// Logs a request JSON payload to the log file (if initialized).
    /// </summary>
    public static void LogRequest(string label, JsonNode? json)
    {
        if (fileWriter == null) return;
        fileWriter.WriteLine($"[ccproxy] >>> {label}:");
        fileWriter.WriteLine(json?.ToJsonString(PrettyOptions) ?? "(null)");
    }

    /// <summary>
    /// Logs a response JSON payload to the log file (if initialized).
    /// </summary>
    public static void LogResponse(string label, JsonNode? json)
    {
        if (fileWriter == null) return;
        fileWriter.WriteLine($"[ccproxy] <<< {label}:");
        fileWriter.WriteLine(json?.ToJsonString(PrettyOptions) ?? "(null)");
    }

    /// <summary>
    /// Logs an SSE event to the log file (if initialized).
    /// </summary>
    public static void LogSseEvent(string direction, string eventType, string data)
    {
        if (fileWriter == null) return;
        fileWriter.WriteLine($"[ccproxy] {direction} SSE event: {eventType}");
        try
        {
            var node = JsonNode.Parse(data);
            fileWriter.WriteLine(node?.ToJsonString(PrettyOptions) ?? data);
        }
        catch
        {
            fileWriter.WriteLine(data);
        }
    }

    /// <summary>
    /// Writes a concise 2-line request summary to stderr.
    /// </summary>
    public static void LogRequestSummary(string method, string path, int statusCode, string model, int toolCount, int toolUseCount, int messageCount)
    {
        Console.Error.WriteLine($"[ccproxy] {method} {path} HTTP/{statusCode}");
        Console.Error.WriteLine($"[ccproxy] {model} -> {toolCount} tools ({toolUseCount} invocations) {messageCount} messages");
    }
}
