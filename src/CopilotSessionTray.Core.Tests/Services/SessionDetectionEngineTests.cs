using System.Text.Json;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="SessionDetectionEngine"/>'s poll-diffing logic using simple in-memory
/// fakes for <see cref="IOpenSessionsRegistryReader"/>/<see cref="IProcessLivenessChecker"/>/
/// <see cref="ISessionEventStreamReader"/>, plus a real <see cref="SessionLockFileInspector"/>
/// pointed at a temp folder (so real lock-file-existence/freshest-per-pid behavior is exercised,
/// not re-faked a second time).
/// </summary>
public sealed class SessionDetectionEngineTests : IDisposable
{
    private readonly string _sessionStateRoot = Path.Combine(Path.GetTempPath(), $"detection-tests-{Guid.NewGuid():N}");
    private readonly FakeRegistryReader _registryReader = new();
    private readonly FakeProcessLivenessChecker _processLivenessChecker = new();
    private readonly FakeEventStreamReader _eventStreamReader = new();

    public SessionDetectionEngineTests()
    {
        Directory.CreateDirectory(_sessionStateRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionStateRoot))
        {
            Directory.Delete(_sessionStateRoot, recursive: true);
        }
    }

    private SessionDetectionEngine CreateEngine() => new(
        _registryReader, new SessionLockFileInspector(_sessionStateRoot), _processLivenessChecker, _eventStreamReader);

    /// <summary>Registers a genuinely-open session: a registry entry, a real lock file naming <paramref name="pid"/>, and that pid marked running.</summary>
    private void AddGenuinelyOpenSession(string sessionId, int pid, bool working, DateTimeOffset? refreshedAtUtc = null)
    {
        _registryReader.Entries[sessionId] = new OpenSessionEntry(
            sessionId, SchemaVersion: 1, OpenedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10), working, refreshedAtUtc ?? DateTimeOffset.UtcNow);

        var sessionDir = Path.Combine(_sessionStateRoot, sessionId);
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, $"inuse.{pid}.lock"), string.Empty);

        _processLivenessChecker.RunningPids.Add(pid);
    }

    [Fact]
    public async Task PollAsync_EmitsStarted_ForNewlyOpenWorkingSession()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal("session-1", change.SessionId);
        Assert.Equal(SessionChangeKind.Started, change.Kind);
        Assert.Equal(SessionStatus.Working, change.Status);
    }

    [Fact]
    public async Task PollAsync_EmitsStarted_WithWaitingForInputStatus_ForNewlyOpenIdleSession()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: false);
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.Started, change.Kind);
        Assert.Equal(SessionStatus.WaitingForInput, change.Status);
    }

    [Fact]
    public async Task PollAsync_EmitsNothing_OnSecondPoll_WhenNothingChanged()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();
        await engine.PollAsync(); // first poll: Started.

        var changes = await engine.PollAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task PollAsync_EmitsFinished_WhenWorkingFlipsToFalse()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();
        await engine.PollAsync(); // first poll: Started (Working).

        _registryReader.Entries["session-1"] = _registryReader.Entries["session-1"] with { Working = false };
        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.Finished, change.Kind);
        Assert.Equal(SessionStatus.Finished, change.Status);
    }

    [Fact]
    public async Task PollAsync_EmitsWorkingStateChanged_WhenWorkingFlipsBackToTrue()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: false);
        var engine = CreateEngine();
        await engine.PollAsync(); // first poll: Started (WaitingForInput).

        _registryReader.Entries["session-1"] = _registryReader.Entries["session-1"] with { Working = true };
        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.WorkingStateChanged, change.Kind);
        Assert.Equal(SessionStatus.Working, change.Status);
    }

    [Fact]
    public async Task PollAsync_EmitsClosed_WhenOwningProcessNoLongerRunning()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();
        await engine.PollAsync(); // first poll: Started.

        _processLivenessChecker.RunningPids.Remove(100);
        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.Closed, change.Kind);
        Assert.Equal(SessionStatus.Closed, change.Status);
    }

    [Fact]
    public async Task PollAsync_EmitsClosed_WhenSessionDisappearsFromRegistryEntirely()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();
        await engine.PollAsync(); // first poll: Started.

        _registryReader.Entries.Remove("session-1");
        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.Closed, change.Kind);
    }

    [Fact]
    public async Task PollAsync_IgnoresSession_WithNoLockFile()
    {
        // Registry entry present, but AddGenuinelyOpenSession's lock file was deliberately never
        // written — mirrors a stale open-sessions-state.json entry the real registry can leave
        // behind for hours, per the "genuinely open" rule this engine deliberately enforces.
        _registryReader.Entries["session-1"] = new OpenSessionEntry(
            "session-1", 1, DateTimeOffset.UtcNow, Working: true, DateTimeOffset.UtcNow);
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task PollAsync_IgnoresSession_WhoseLockedPidIsNotRunning()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        _processLivenessChecker.RunningPids.Clear(); // lock file exists, but nothing reports that pid as alive.
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task PollAsync_KeepsOnlyFreshestSession_WhenTwoSessionsShareSamePid()
    {
        // Simulates a /resume or /fork: one pid, two session folders' lock files, only the
        // freshest should count as genuinely open.
        AddGenuinelyOpenSession("old-session", pid: 100, working: true, refreshedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));
        AddGenuinelyOpenSession("new-session", pid: 100, working: true, refreshedAtUtc: DateTimeOffset.UtcNow);
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal("new-session", change.SessionId);
        Assert.Equal(SessionChangeKind.Started, change.Kind);
    }

    [Fact]
    public async Task PollAsync_AttachesLatestEvent_FromEventStreamReader_AsEnrichment()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var expectedEvent = new SessionEventRecord(SessionEventKind.AssistantTurnStart, "assistant.turn_start", DateTimeOffset.UtcNow, "{}");
        _eventStreamReader.EventsBySession["session-1"] = [expectedEvent];
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Same(expectedEvent, change.LatestEvent);
    }

    [Fact]
    public async Task PollAsync_LeavesLatestEventNull_WhenEventStreamReaderThrows()
    {
        // Signal authority rule: a failure reading events.jsonl is enrichment-only and must never
        // stop the engine from reporting the correct status from the registry/process check.
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        _eventStreamReader.ThrowForSessions.Add("session-1");
        var engine = CreateEngine();

        var changes = await engine.PollAsync();

        var change = Assert.Single(changes);
        Assert.Equal(SessionChangeKind.Started, change.Kind); // status still correctly reported...
        Assert.Null(change.LatestEvent); // ...even though enrichment silently failed.
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private static SessionEventRecord Event(string type, string dataJson, DateTimeOffset at) =>
        new(SessionEventKind.Unknown, type, at, $"{{\"type\":\"{type}\",\"timestamp\":\"{at:O}\",\"data\":{dataJson}}}");

    private static SessionEventRecord UserMessage(DateTimeOffset at, string? source = null) =>
        Event("user.message", source is null ? "{\"content\":\"do it\"}" : $"{{\"content\":\"continue\",\"source\":\"{source}\"}}", at);

    private static SessionEventRecord AssistantMessage(DateTimeOffset at, string content, string? parentToolCallId = null) =>
        Event("assistant.message", $"{{\"content\":{JsonSerializer.Serialize(content)}{(parentToolCallId is null ? string.Empty : $",\"parentToolCallId\":\"{parentToolCallId}\"")}}}", at);

    private static SessionEventRecord TaskComplete(DateTimeOffset at, string summary) =>
        Event("session.task_complete", $"{{\"success\":true,\"summary\":{JsonSerializer.Serialize(summary)}}}", at);

    private async Task<SessionChangeEvent> RunUntilFinished(
        IReadOnlyList<SessionEventRecord>[] batchesWhileWorking, IReadOnlyList<SessionEventRecord> finishingBatch)
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        _eventStreamReader.BatchesBySession["session-1"] = new Queue<IReadOnlyList<SessionEventRecord>>(batchesWhileWorking.Append(finishingBatch));
        var engine = CreateEngine();
        await engine.PollAsync();
        for (var i = 1; i < batchesWhileWorking.Length; i++)
        {
            await engine.PollAsync();
        }

        _registryReader.Entries["session-1"] = _registryReader.Entries["session-1"] with { Working = false };
        var changes = await engine.PollAsync();
        return Assert.Single(changes);
    }

    [Fact]
    public async Task Finished_CarriesTheTaskCompleteSummary_AsTheOutcome()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), AssistantMessage(T0.AddSeconds(5), "Looking into it."), TaskComplete(T0.AddSeconds(50), "  Fixed the flaky test.  ")]],
            []);

        Assert.Equal(SessionChangeKind.Finished, finished.Kind);
        Assert.Equal("Fixed the flaky test.", finished.Outcome);
    }

    [Fact]
    public async Task Finished_UsesTheLastMainAgentMessage_WhenThereIsNoTaskComplete()
    {
        var finished = await RunUntilFinished(
            [[
                UserMessage(T0),
                AssistantMessage(T0.AddSeconds(5), "Let me check the logs."),
                AssistantMessage(T0.AddSeconds(20), "The retry count was wrong; it is fixed now."),
                AssistantMessage(T0.AddSeconds(21), "A sub-agent's chatter must not win.", parentToolCallId: "call-1"),
                AssistantMessage(T0.AddSeconds(22), "   "),
            ]],
            []);

        Assert.Equal("The retry count was wrong; it is fixed now.", finished.Outcome);
    }

    [Fact]
    public async Task Finished_PrefersTheTaskCompleteSummary_OverAnAssistantMessage()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), TaskComplete(T0.AddSeconds(40), "Summary from task_complete"), AssistantMessage(T0.AddSeconds(41), "Final chat message")]],
            []);

        Assert.Equal("Summary from task_complete", finished.Outcome);
    }

    [Fact]
    public async Task Finished_ReportsABlockedTaskComplete_WithItsReason()
    {
        var blocked = Event("session.task_complete", "{\"success\":false,\"outcome\":\"blocked\",\"reason\":\"needs a database password\",\"blocker\":\"credentials\"}", T0.AddSeconds(40));

        var finished = await RunUntilFinished([[UserMessage(T0), blocked]], []);

        Assert.Equal("Blocked: needs a database password", finished.Outcome);
    }

    [Fact]
    public async Task Finished_RemembersTheOutcome_SeenInAnEarlierPoll()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0)], [AssistantMessage(T0.AddSeconds(40), "Done in the earlier poll.")]],
            [Event("assistant.turn_end", "{}", T0.AddSeconds(41)), Event("session.usage_checkpoint", "{}", T0.AddSeconds(41))]);

        Assert.Equal("Done in the earlier poll.", finished.Outcome);
        Assert.Equal("session.usage_checkpoint", finished.LatestEvent!.RawType);
    }

    [Fact]
    public async Task Outcome_StartsOverWithEachRealUserMessage()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), TaskComplete(T0.AddSeconds(30), "First turn's result")], [UserMessage(T0.AddMinutes(5))]],
            []);

        Assert.Null(finished.Outcome);
    }

    [Theory]
    [InlineData("autopilot")]
    [InlineData("system")]
    [InlineData("agent-0ec123")]
    public async Task Outcome_IsNotResetBy_MessagesThatDidNotComeFromTheUser(string source)
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), TaskComplete(T0.AddSeconds(30), "Result so far")], [UserMessage(T0.AddMinutes(1), source)]],
            []);

        Assert.Equal("Result so far", finished.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), finished.WorkedFor);
    }

    [Fact]
    public async Task Outcome_IsDroppedWhenTheTurnWasAborted()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), AssistantMessage(T0.AddSeconds(5), "I'll start by reading the file."), Event("abort", "{}", T0.AddSeconds(6))]],
            []);

        Assert.Null(finished.Outcome);
    }

    [Fact]
    public async Task WorkedFor_IsTheTimeFromTheLastRealUserMessageToTheLastEventSeen()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0.AddMinutes(-30), "autopilot"), UserMessage(T0), AssistantMessage(T0.AddMinutes(1), "step")]],
            [TaskComplete(T0.AddMinutes(12), "done"), Event("assistant.turn_end", "{}", T0.AddMinutes(12).AddSeconds(1))]);

        Assert.Equal(TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(1)), finished.WorkedFor);
    }

    [Fact]
    public async Task WorkedFor_IsUnknown_WhenNoUserMessageWasSeen()
    {
        var finished = await RunUntilFinished([[AssistantMessage(T0, "Midway.")]], [TaskComplete(T0.AddMinutes(2), "done")]);

        Assert.Null(finished.WorkedFor);
        Assert.Equal("done", finished.Outcome);
    }

    [Fact]
    public async Task WorkedFor_CountsOnlyTheCurrentTurn_WhenTheSessionHasHadEarlierOnes()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), TaskComplete(T0.AddMinutes(10), "First turn")], [UserMessage(T0.AddHours(2))]],
            [TaskComplete(T0.AddHours(2).AddMinutes(3), "Second turn")]);

        Assert.Equal(TimeSpan.FromMinutes(3), finished.WorkedFor);
        Assert.Equal("Second turn", finished.Outcome);
    }

    [Fact]
    public async Task WorkedFor_IsUnknown_WhenTheLatestEventIsOlderThanTheUserMessage()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0.AddMinutes(10)), Event("assistant.turn_end", "{}", T0)]],
            []);

        Assert.Null(finished.WorkedFor);
    }

    [Fact]
    public async Task Outcome_DropsAnEarlierTurnsLastReply_WhenANewUserMessageArrives()
    {
        var finished = await RunUntilFinished(
            [[UserMessage(T0), AssistantMessage(T0.AddSeconds(30), "First turn's reply")], [UserMessage(T0.AddMinutes(5))]],
            []);

        Assert.Null(finished.Outcome);
    }

    [Theory]
    [InlineData("\"parentToolCallId\":null")]
    [InlineData("\"parentToolCallId\":\"\"")]
    public async Task Finished_CountsAMainAgentMessage_WhoseParentToolCallIdIsNullOrEmpty(string parentField)
    {
        var message = Event("assistant.message", $"{{\"content\":\"Main reply\",{parentField}}}", T0.AddSeconds(5));

        var finished = await RunUntilFinished([[UserMessage(T0), message]], []);

        Assert.Equal("Main reply", finished.Outcome);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task Outcome_IsResetBy_AUserMessageWhoseSourceIsNullOrEmpty(string source)
    {
        var typed = Event("user.message", $"{{\"content\":\"do it\",\"source\":{source}}}", T0.AddMinutes(5));

        var finished = await RunUntilFinished(
            [[UserMessage(T0), TaskComplete(T0.AddSeconds(30), "Earlier result")], [typed]],
            []);

        Assert.Null(finished.Outcome);
    }

    [Fact]
    public async Task Outcome_IsForgotten_WhenTheSessionClosesAndLaterReopens()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        _eventStreamReader.BatchesBySession["session-1"] = new Queue<IReadOnlyList<SessionEventRecord>>([[UserMessage(T0), TaskComplete(T0.AddSeconds(30), "Old run's result")]]);
        var engine = CreateEngine();
        await engine.PollAsync();
        _processLivenessChecker.RunningPids.Remove(100);
        File.Delete(Path.Combine(_sessionStateRoot, "session-1", "inuse.100.lock"));
        Assert.Equal(SessionChangeKind.Closed, Assert.Single(await engine.PollAsync()).Kind);

        AddGenuinelyOpenSession("session-1", pid: 200, working: true);
        Assert.Equal(SessionChangeKind.Started, Assert.Single(await engine.PollAsync()).Kind);
        _registryReader.Entries["session-1"] = _registryReader.Entries["session-1"] with { Working = false };
        var finished = Assert.Single(await engine.PollAsync());

        Assert.Equal(SessionChangeKind.Finished, finished.Kind);
        Assert.Null(finished.Outcome);
    }

    [Fact]
    public async Task Finished_HasNoOutcome_WhenEventsAreUnparseable()
    {
        var garbage = new SessionEventRecord(SessionEventKind.Unknown, "assistant.message", T0, "{ not json");
        var noData = new SessionEventRecord(SessionEventKind.Unknown, "session.task_complete", T0, "{\"type\":\"session.task_complete\"}");

        var finished = await RunUntilFinished([[garbage, noData]], []);

        Assert.Null(finished.Outcome);
        Assert.Null(finished.WorkedFor);
    }

    [Fact]
    public async Task Finished_StillReportsTheTransition_WhenEventReadsFail()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        var engine = CreateEngine();
        await engine.PollAsync();
        _eventStreamReader.ThrowForSessions.Add("session-1");
        _registryReader.Entries["session-1"] = _registryReader.Entries["session-1"] with { Working = false };

        var finished = Assert.Single(await engine.PollAsync());

        Assert.Equal(SessionChangeKind.Finished, finished.Kind);
        Assert.Null(finished.Outcome);
    }

    [Fact]
    public async Task Closed_CarriesNoOutcome()
    {
        AddGenuinelyOpenSession("session-1", pid: 100, working: true);
        _eventStreamReader.BatchesBySession["session-1"] = new Queue<IReadOnlyList<SessionEventRecord>>([[UserMessage(T0), TaskComplete(T0.AddSeconds(30), "Done")]]);
        var engine = CreateEngine();
        await engine.PollAsync();
        _processLivenessChecker.RunningPids.Remove(100);

        var closed = Assert.Single(await engine.PollAsync());

        Assert.Equal(SessionChangeKind.Closed, closed.Kind);
        Assert.Null(closed.Outcome);
    }

    private sealed class FakeRegistryReader : IOpenSessionsRegistryReader
    {
        public Dictionary<string, OpenSessionEntry> Entries { get; } = new();

        public Task<IReadOnlyDictionary<string, OpenSessionEntry>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, OpenSessionEntry>>(new Dictionary<string, OpenSessionEntry>(Entries));
    }

    private sealed class FakeProcessLivenessChecker : IProcessLivenessChecker
    {
        public HashSet<int> RunningPids { get; } = new();

        public bool IsProcessRunning(int processId) => RunningPids.Contains(processId);

        public IReadOnlyList<int> GetRunningCopilotProcessIds() => RunningPids.ToList();
    }

    private sealed class FakeEventStreamReader : ISessionEventStreamReader
    {
        public Dictionary<string, IReadOnlyList<SessionEventRecord>> EventsBySession { get; } = new();

        public Dictionary<string, Queue<IReadOnlyList<SessionEventRecord>>> BatchesBySession { get; } = new();

        public HashSet<string> ThrowForSessions { get; } = new();

        public Task<SessionEventReadResult> ReadNewEventsAsync(
            string sessionId, long fromByteOffset, CancellationToken cancellationToken = default)
        {
            if (ThrowForSessions.Contains(sessionId))
            {
                throw new IOException("Simulated events.jsonl read failure.");
            }

            if (BatchesBySession.TryGetValue(sessionId, out var batches))
            {
                var batch = batches.Count > 0 ? batches.Dequeue() : Array.Empty<SessionEventRecord>();
                return Task.FromResult(new SessionEventReadResult(batch, fromByteOffset + 1));
            }

            var events = EventsBySession.TryGetValue(sessionId, out var configured) ? configured : Array.Empty<SessionEventRecord>();
            return Task.FromResult(new SessionEventReadResult(events, fromByteOffset + 1));
        }
    }
}
