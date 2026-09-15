namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A single parsed line from a session's <c>events.jsonl</c> file.
/// </summary>
/// <param name="Kind">The recognized event kind, or <see cref="SessionEventKind.Unknown"/>.</param>
/// <param name="RawType">The raw, unparsed "type" field exactly as it appears in the file.</param>
/// <param name="TimestampUtc">Event timestamp, if present in the payload.</param>
/// <param name="RawJson">The full raw JSON line, preserved for forward-compatibility and diagnostics.</param>
public sealed record SessionEventRecord(
    SessionEventKind Kind,
    string RawType,
    DateTimeOffset? TimestampUtc,
    string RawJson);
