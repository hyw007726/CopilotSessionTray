using System.Text;
using System.Text.Json;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="ISessionEventStreamReader"/> — tails a session's
/// <c>events.jsonl</c> under <c>%USERPROFILE%\.copilot\session-state\&lt;sessionId&gt;\</c>.
/// Real on-disk shape (top-level <c>type</c>/<c>timestamp</c>/<c>data</c>/<c>id</c>/<c>parentId</c>
/// fields per line) confirmed against a real, populated file while implementing this — see
/// IMPLEMENTATION_PLAN.md §2.4/§9.1.
/// </summary>
public sealed class SessionEventStreamReader : ISessionEventStreamReader
{
    private static readonly string DefaultSessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private readonly string _sessionStateRoot;

    /// <summary>
    /// Creates a reader against the real <c>session-state</c> folder. An explicit
    /// <paramref name="sessionStateRoot"/> override exists solely so xUnit tests can point this at
    /// a temp fixture folder instead of the real <c>.copilot</c> folder — every real call site
    /// uses the parameterless default.
    /// </summary>
    public SessionEventStreamReader(string? sessionStateRoot = null)
    {
        _sessionStateRoot = sessionStateRoot ?? DefaultSessionStateRoot;
    }

    public async Task<SessionEventReadResult> ReadNewEventsAsync(
        string sessionId, long fromByteOffset, CancellationToken cancellationToken = default)
    {
        var eventsFilePath = Path.Combine(_sessionStateRoot, sessionId, "events.jsonl");
        if (!File.Exists(eventsFilePath))
        {
            return new SessionEventReadResult(Array.Empty<SessionEventRecord>(), fromByteOffset);
        }

        byte[] buffer;
        long startOffset;
        await using (var stream = new FileStream(eventsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // Defensive: events.jsonl is documented as append-only and should never shrink, but a
            // stale/out-of-range offset (e.g. the file was somehow replaced) is resynced to the
            // current end rather than throwing on a negative-length Seek/read.
            startOffset = fromByteOffset >= 0 && fromByteOffset <= stream.Length ? fromByteOffset : stream.Length;

            var remaining = stream.Length - startOffset;
            if (remaining <= 0)
            {
                return new SessionEventReadResult(Array.Empty<SessionEventRecord>(), startOffset);
            }

            stream.Seek(startOffset, SeekOrigin.Begin);
            buffer = new byte[remaining];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead < buffer.Length)
            {
                Array.Resize(ref buffer, totalRead);
            }
        }

        // Only bytes up to and including the last '\n' are complete lines — anything after (a
        // line still being appended by a live Copilot CLI process) must wait for the next poll,
        // both to avoid parsing truncated JSON and so NextByteOffset never skips past it.
        // Scanning the raw bytes (not the decoded string) for 0x0A is what keeps this byte-exact
        // for UTF-8 content containing multi-byte characters (e.g. emoji in a message body):
        // 0x0A only ever appears as an actual newline in UTF-8, never inside a multi-byte
        // sequence, so this can't misalign the offset the way scanning a decoded char index could.
        var lastNewlineIndex = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewlineIndex < 0)
        {
            return new SessionEventReadResult(Array.Empty<SessionEventRecord>(), startOffset);
        }

        var completeByteCount = lastNewlineIndex + 1;
        var text = Encoding.UTF8.GetString(buffer, 0, completeByteCount);

        var events = new List<SessionEventRecord>();
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var record = TryParseEventLine(line);
            if (record is not null)
            {
                events.Add(record);
            }
        }

        return new SessionEventReadResult(events, startOffset + completeByteCount);
    }

    /// <summary>
    /// Parses one raw <c>events.jsonl</c> line, or returns null for a line that isn't valid JSON
    /// at all — per this interface's documented contract, such lines are skipped entirely rather
    /// than surfaced as an <see cref="SessionEventKind.Unknown"/> record, since there's no
    /// meaningful <c>type</c>/<c>timestamp</c> to extract from them. Internal (not private) so
    /// xUnit can exercise the parsing/mapping logic directly without needing a real file on disk.
    /// </summary>
    internal static SessionEventRecord? TryParseEventLine(string rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var rawType = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeProperty) &&
                typeProperty.ValueKind == JsonValueKind.String
                    ? typeProperty.GetString() ?? string.Empty
                    : string.Empty;

            DateTimeOffset? timestamp = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("timestamp", out var timestampProperty) &&
                timestampProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(timestampProperty.GetString(), out var parsedTimestamp)
                    ? parsedTimestamp
                    : null;

            return new SessionEventRecord(MapEventKind(rawType), rawType, timestamp, rawJson);
        }
    }

    /// <summary>
    /// Maps a raw <c>events.jsonl</c> "type" string to its <see cref="SessionEventKind"/>, or
    /// <see cref="SessionEventKind.Unknown"/> for anything not in the observed set documented in
    /// IMPLEMENTATION_PLAN.md §2.4 — real files also contain many other types (e.g.
    /// <c>user.message</c>, <c>model.turn_started</c>, <c>permission.requested</c>,
    /// <c>session.mode_changed</c>) that are deliberately left as <see cref="SessionEventKind.Unknown"/>
    /// since nothing in this app's detection model currently needs them; degrading unrecognized
    /// types gracefully (rather than throwing) is exactly what lets future/unmapped event types
    /// from newer Copilot CLI versions not break this reader.
    /// </summary>
    internal static SessionEventKind MapEventKind(string rawType) => rawType switch
    {
        "assistant.turn_start" => SessionEventKind.AssistantTurnStart,
        "assistant.message" => SessionEventKind.AssistantMessage,
        "tool.execution_start" => SessionEventKind.ToolExecutionStart,
        "tool.execution_complete" => SessionEventKind.ToolExecutionComplete,
        "assistant.turn_end" => SessionEventKind.AssistantTurnEnd,
        "session.task_complete" => SessionEventKind.SessionTaskComplete,
        "session.usage_checkpoint" => SessionEventKind.SessionUsageCheckpoint,
        "session.shutdown" => SessionEventKind.SessionShutdown,
        _ => SessionEventKind.Unknown,
    };
}
