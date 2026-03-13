namespace CCProxy.Proxy;

/// <summary>Represents a single Server-Sent Event with its event type and data payload.</summary>
public record SseEvent(string EventType, string Data);

/// <summary>
/// Parses a stream of Server-Sent Events (SSE) according to the SSE protocol.
/// Events are delimited by blank lines; each event has an <c>event:</c> type and <c>data:</c> payload.
/// </summary>
public static class SseReader
{
    /// <summary>
    /// Asynchronously reads SSE events from the given stream.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new StreamReader(stream);
        string? eventType = null;
        string? data = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break; // End of stream
            }

            if (line.StartsWith("event:"))
            {
                eventType = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:"))
            {
                data = line["data:".Length..].Trim();
            }
            else if (line == string.Empty)
            {
                // Empty line = end of event
                if (eventType != null && data != null)
                {
                    yield return new SseEvent(eventType, data);
                }
                eventType = null;
                data = null;
            }
        }

        // Handle final event without trailing newline
        if (eventType != null && data != null)
        {
            yield return new SseEvent(eventType, data);
        }
    }
}
