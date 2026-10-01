using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="SessionEventStreamReader"/> against a throwaway temp fixture folder (via
/// its testability constructor overload) standing in for <c>.copilot\session-state</c>, using the
/// exact real <c>events.jsonl</c> line shape observed on a real machine (top-level
/// <c>type</c>/<c>timestamp</c>/<c>data</c>/<c>id</c>/<c>parentId</c> fields — see
/// IMPLEMENTATION_PLAN.md §2.4/§9.1).
/// </summary>
public sealed class SessionEventStreamReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"events-tests-{Guid.NewGuid():N}");

    public SessionEventStreamReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionEventStreamReader CreateReader() => new(_root);

    private string WriteEventsFile(string sessionId, string content)
    {
        var sessionDir = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "events.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ReadNewEventsAsync_ReturnsEmpty_WhenSessionFolderDoesNotExist()
    {
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("does-not-exist", fromByteOffset: 0);

        Assert.Empty(result.Events);
        Assert.Equal(0, result.NextByteOffset);
    }

    [Fact]
    public async Task ReadNewEventsAsync_ParsesRealObservedShape_MappingKnownTypes()
    {
        WriteEventsFile("session-1",
            """
            {"type":"assistant.turn_start","data":{"turnId":"0"},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}
            {"type":"session.task_complete","data":{"summary":"Done"},"id":"b","timestamp":"2026-09-02T13:28:30.000Z","parentId":"a"}
            {"type":"assistant.turn_end","data":{"turnId":"0"},"id":"c","timestamp":"2026-09-02T13:28:30.100Z","parentId":"b"}

            """);
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: 0);

        Assert.Equal(3, result.Events.Count);
        Assert.Equal(SessionEventKind.AssistantTurnStart, result.Events[0].Kind);
        Assert.Equal(SessionEventKind.SessionTaskComplete, result.Events[1].Kind);
        Assert.Equal(SessionEventKind.AssistantTurnEnd, result.Events[2].Kind);
        Assert.Equal("assistant.turn_end", result.Events[2].RawType);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T13:28:30.100Z"), result.Events[2].TimestampUtc);
    }

    [Fact]
    public async Task ReadNewEventsAsync_MapsUnrecognizedType_ToUnknown()
    {
        WriteEventsFile("session-1", """{"type":"user.message","data":{},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}""" + "\n");
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: 0);

        Assert.Single(result.Events);
        Assert.Equal(SessionEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal("user.message", result.Events[0].RawType);
    }

    [Fact]
    public async Task ReadNewEventsAsync_Skips_LinesThatAreNotValidJson()
    {
        WriteEventsFile("session-1",
            "not valid json at all\n" +
            """{"type":"session.shutdown","data":{},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}""" + "\n");
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: 0);

        Assert.Single(result.Events);
        Assert.Equal(SessionEventKind.SessionShutdown, result.Events[0].Kind);
    }

    [Fact]
    public async Task ReadNewEventsAsync_DoesNotConsume_TrailingPartialLineMissingNewline()
    {
        // Simulates reading mid-write: the last line has no trailing '\n' yet because Copilot CLI
        // hasn't finished appending it — must not be parsed, and NextByteOffset must not advance
        // past the completed lines, so the next poll re-reads (and this time completes) it.
        var completeLine = """{"type":"assistant.message","data":{},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}""" + "\n";
        var partialLine = """{"type":"assistant.turn_end","data":{"turnId":"0"}""";
        WriteEventsFile("session-1", completeLine + partialLine);
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: 0);

        Assert.Single(result.Events);
        Assert.Equal(SessionEventKind.AssistantMessage, result.Events[0].Kind);
        Assert.Equal(completeLine.Length, result.NextByteOffset);
    }

    [Fact]
    public async Task ReadNewEventsAsync_ResumesFromGivenOffset_WithoutReReadingEarlierLines()
    {
        var firstLine = """{"type":"assistant.turn_start","data":{},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}""" + "\n";
        var secondLine = """{"type":"assistant.turn_end","data":{},"id":"b","timestamp":"2026-09-02T13:28:25.042Z","parentId":"a"}""" + "\n";
        WriteEventsFile("session-1", firstLine + secondLine);
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: firstLine.Length);

        Assert.Single(result.Events);
        Assert.Equal(SessionEventKind.AssistantTurnEnd, result.Events[0].Kind);
        Assert.Equal(firstLine.Length + secondLine.Length, result.NextByteOffset);
    }

    [Fact]
    public async Task ReadNewEventsAsync_ReturnsEmpty_WhenNoNewLinesSinceOffset()
    {
        var onlyLine = """{"type":"session.shutdown","data":{},"id":"a","timestamp":"2026-09-02T13:28:24.042Z","parentId":null}""" + "\n";
        WriteEventsFile("session-1", onlyLine);
        ISessionEventStreamReader reader = CreateReader();

        var result = await reader.ReadNewEventsAsync("session-1", fromByteOffset: onlyLine.Length);

        Assert.Empty(result.Events);
        Assert.Equal(onlyLine.Length, result.NextByteOffset);
    }

    [Fact]
    public async Task ReadNewEventsAsync_HandlesMultiByteUtf8Characters_WithByteExactOffsets()
    {
        // Emoji/CJK content encodes as multiple UTF-8 bytes per character — this must not
        // misalign NextByteOffset the way scanning a decoded char index instead of raw bytes
        // would (see the reader's own doc comment on why it scans bytes, not the decoded string).
        // Built with a normal (non-raw) string so `\ud83c\udf89`/`\u65e5...` are actually
        // interpreted as C# Unicode escapes into real surrogate-pair/BMP characters — a `"""..."""`
        // raw string literal would instead keep them as literal backslash-u-hex text (all ASCII),
        // silently defeating the point of this test.
        var emoji = "\ud83c\udf89"; // 🎉 — a surrogate pair: 2 UTF-16 chars, but 4 UTF-8 bytes.
        var japanese = "\u65e5\u672c\u8a9e"; // 日本語 — 3 BMP chars, but 3 bytes each in UTF-8.
        var firstLine = "{\"type\":\"assistant.message\",\"data\":{\"content\":\"done " +
            emoji + " " + japanese + "\"},\"id\":\"a\",\"timestamp\":\"2026-09-02T13:28:24.042Z\",\"parentId\":null}\n";
        var secondLine = """{"type":"session.shutdown","data":{},"id":"b","timestamp":"2026-09-02T13:28:25.042Z","parentId":"a"}""" + "\n";
        var firstLineByteLength = System.Text.Encoding.UTF8.GetByteCount(firstLine);
        // Sanity-check the fixture itself actually exercises multi-byte content — otherwise this
        // test could silently pass for the wrong reason (as it did before this fix).
        Assert.True(firstLineByteLength > firstLine.Length);

        // Written incrementally (not as one combined file) to genuinely simulate two separate
        // polls, each only seeing what had been appended by that point.
        WriteEventsFile("session-1", firstLine);
        ISessionEventStreamReader reader = CreateReader();
        var firstResult = await reader.ReadNewEventsAsync("session-1", fromByteOffset: 0);

        File.AppendAllText(Path.Combine(_root, "session-1", "events.jsonl"), secondLine);
        var secondResult = await reader.ReadNewEventsAsync("session-1", fromByteOffset: firstResult.NextByteOffset);

        Assert.Equal(firstLineByteLength, firstResult.NextByteOffset);
        Assert.Single(secondResult.Events);
        Assert.Equal(SessionEventKind.SessionShutdown, secondResult.Events[0].Kind);
    }

    [Fact]
    public void MapEventKind_MapsAllDocumentedRealTypes()
    {
        Assert.Equal(SessionEventKind.AssistantTurnStart, SessionEventStreamReader.MapEventKind("assistant.turn_start"));
        Assert.Equal(SessionEventKind.AssistantMessage, SessionEventStreamReader.MapEventKind("assistant.message"));
        Assert.Equal(SessionEventKind.ToolExecutionStart, SessionEventStreamReader.MapEventKind("tool.execution_start"));
        Assert.Equal(SessionEventKind.ToolExecutionComplete, SessionEventStreamReader.MapEventKind("tool.execution_complete"));
        Assert.Equal(SessionEventKind.AssistantTurnEnd, SessionEventStreamReader.MapEventKind("assistant.turn_end"));
        Assert.Equal(SessionEventKind.SessionTaskComplete, SessionEventStreamReader.MapEventKind("session.task_complete"));
        Assert.Equal(SessionEventKind.SessionUsageCheckpoint, SessionEventStreamReader.MapEventKind("session.usage_checkpoint"));
        Assert.Equal(SessionEventKind.SessionShutdown, SessionEventStreamReader.MapEventKind("session.shutdown"));
        Assert.Equal(SessionEventKind.Unknown, SessionEventStreamReader.MapEventKind("some.future.type"));
    }

    [Fact]
    public void TryParseEventLine_ReturnsNull_ForNonJsonLine()
    {
        Assert.Null(SessionEventStreamReader.TryParseEventLine("not json"));
    }

    [Fact]
    public void TryParseEventLine_ReturnsNullTimestamp_WhenTimestampFieldIsMissing()
    {
        var record = SessionEventStreamReader.TryParseEventLine("""{"type":"session.shutdown","data":{}}""");

        Assert.NotNull(record);
        Assert.Null(record!.TimestampUtc);
        Assert.Equal(SessionEventKind.SessionShutdown, record.Kind);
    }
}
