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

        public HashSet<string> ThrowForSessions { get; } = new();

        public Task<SessionEventReadResult> ReadNewEventsAsync(
            string sessionId, long fromByteOffset, CancellationToken cancellationToken = default)
        {
            if (ThrowForSessions.Contains(sessionId))
            {
                throw new IOException("Simulated events.jsonl read failure.");
            }

            var events = EventsBySession.TryGetValue(sessionId, out var configured) ? configured : Array.Empty<SessionEventRecord>();
            return Task.FromResult(new SessionEventReadResult(events, fromByteOffset + 1));
        }
    }
}
