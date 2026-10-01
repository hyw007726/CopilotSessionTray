using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="ISessionDetectionEngine"/> — the "brain" that combines
/// <see cref="IOpenSessionsRegistryReader"/>, <see cref="SessionLockFileInspector"/>,
/// <see cref="IProcessLivenessChecker"/>, and <see cref="ISessionEventStreamReader"/> across
/// successive <see cref="PollAsync"/> calls to detect state transitions.
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts as "genuinely open" — same rule as <c>TrayViewModel.LoadLiveSessionsScenarioAsync</c>'s
/// live-session view, deliberately kept identical (see IMPLEMENTATION_PLAN.md §9.1):</b> presence
/// in <c>open-sessions-state.json</c> alone is not enough, since entries can sit there stale for
/// hours, and a single long-lived process can hold <c>inuse.&lt;pid&gt;.lock</c> files in multiple
/// old session folders after a <c>/resume</c>/<c>/fork</c>. A session only counts as open here if
/// (1) its lock file exists and that pid is currently running, and (2) it's the freshest such
/// session for that pid.
/// </para>
/// <para>
/// <b>Signal authority (IMPLEMENTATION_PLAN.md §3/§9's "stuck state machine" risk):</b> whether a
/// session is <see cref="SessionStatus.Working"/> is always re-derived from
/// <see cref="OpenSessionEntry.Working"/> + process liveness on every single poll — never solely
/// from an <c>events.jsonl</c> event type. This is what makes the detection self-correcting: even
/// if some exit path never emits an expected "turn ended" event (the exact bug that left GitHub's
/// own official taskbar-presence feature stuck spinning forever — see
/// <see href="https://github.com/github/copilot-cli/issues/4771"/>), the very next poll's registry
/// read still reflects reality. <c>events.jsonl</c> is tailed purely for the
/// <see cref="SessionChangeEvent.LatestEvent"/> enrichment detail, never to decide status.
/// </para>
/// <para>
/// <see cref="SessionChangeKind"/> selection per still-open session, comparing this poll's
/// snapshot against the previous one held in memory:
/// <list type="bullet">
/// <item>Not seen last poll → <see cref="SessionChangeKind.Started"/>.</item>
/// <item>Previously working, now not → <see cref="SessionChangeKind.Finished"/> (the "candidate
/// for notification" transition — status becomes <see cref="SessionStatus.Finished"/>, not merely
/// <see cref="SessionStatus.WaitingForInput"/>, so the caller can tell "just finished" apart from
/// "has been idle a while" using only this one event, since steady-state idling produces no event
/// at all — see the interface's "empty if nothing changed" contract).</item>
/// <item>Previously not working, now working → <see cref="SessionChangeKind.WorkingStateChanged"/>
/// (started a new turn, e.g. after the user replied).</item>
/// <item>No flag change → no event for that session this poll.</item>
/// </list>
/// A session seen last poll but no longer in this poll's genuinely-open set → <see cref="SessionChangeKind.Closed"/>.
/// This also covers the "superseded" case (an older session sharing a pid with a fresher one) —
/// not a literal process exit, but from that specific session id's own tracked perspective it has
/// equally stopped being an active, open session, and the enum offers no separate "superseded"
/// case to distinguish it.
/// </para>
/// </remarks>
public sealed class SessionDetectionEngine : ISessionDetectionEngine
{
    private readonly IOpenSessionsRegistryReader _registryReader;
    private readonly SessionLockFileInspector _lockFileInspector;
    private readonly IProcessLivenessChecker _processLivenessChecker;
    private readonly ISessionEventStreamReader _eventStreamReader;

    /// <summary>Previous poll's genuinely-open sessions, keyed by session id — the diff baseline for the next call.</summary>
    private readonly Dictionary<string, TrackedSession> _tracked = new();

    /// <summary>Per-session <c>events.jsonl</c> tailing offset, so repeated polls don't re-read already-seen lines.</summary>
    private readonly Dictionary<string, long> _eventOffsets = new();

    public SessionDetectionEngine(
        IOpenSessionsRegistryReader registryReader,
        SessionLockFileInspector lockFileInspector,
        IProcessLivenessChecker processLivenessChecker,
        ISessionEventStreamReader eventStreamReader)
    {
        _registryReader = registryReader;
        _lockFileInspector = lockFileInspector;
        _processLivenessChecker = processLivenessChecker;
        _eventStreamReader = eventStreamReader;
    }

    public async Task<IReadOnlyList<SessionChangeEvent>> PollAsync(CancellationToken cancellationToken = default)
    {
        var registry = await _registryReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var runningPids = new HashSet<int>(_processLivenessChecker.GetRunningCopilotProcessIds());

        // Same "genuinely open" resolution as TrayViewModel.LoadLiveSessionsScenarioAsync — see
        // the class doc comment above for why presence in the registry alone isn't sufficient.
        var genuinelyOpen = registry.Values
            .Select(entry => (Entry: entry, OwningPid: _lockFileInspector.GetOwningProcessId(entry.SessionId)))
            .Where(x => x.OwningPid.HasValue && runningPids.Contains(x.OwningPid.Value))
            .GroupBy(x => x.OwningPid!.Value)
            .Select(group => group.OrderByDescending(x => x.Entry.RefreshedAtUtc).First().Entry)
            .ToDictionary(entry => entry.SessionId);

        var now = DateTimeOffset.UtcNow;
        var changes = new List<SessionChangeEvent>();

        foreach (var (sessionId, entry) in genuinelyOpen)
        {
            var latestEvent = await TryReadLatestEventAsync(sessionId, cancellationToken).ConfigureAwait(false);

            if (!_tracked.TryGetValue(sessionId, out var previous))
            {
                _tracked[sessionId] = new TrackedSession(entry.Working);
                var startedStatus = entry.Working ? SessionStatus.Working : SessionStatus.WaitingForInput;
                changes.Add(new SessionChangeEvent(sessionId, SessionChangeKind.Started, startedStatus, now, latestEvent));
                continue;
            }

            if (previous.WasWorking && !entry.Working)
            {
                _tracked[sessionId] = previous with { WasWorking = false };
                changes.Add(new SessionChangeEvent(sessionId, SessionChangeKind.Finished, SessionStatus.Finished, now, latestEvent));
            }
            else if (!previous.WasWorking && entry.Working)
            {
                _tracked[sessionId] = previous with { WasWorking = true };
                changes.Add(new SessionChangeEvent(sessionId, SessionChangeKind.WorkingStateChanged, SessionStatus.Working, now, latestEvent));
            }

            // No flag change: nothing to report for this session this poll, per this interface's
            // documented "empty if nothing changed" contract.
        }

        // Anything tracked last poll but missing from this poll's genuinely-open set has closed
        // (process exited, lock file gone, or superseded — see the class doc comment).
        foreach (var sessionId in _tracked.Keys.Where(id => !genuinelyOpen.ContainsKey(id)).ToList())
        {
            _tracked.Remove(sessionId);
            var latestEvent = await TryReadLatestEventAsync(sessionId, cancellationToken).ConfigureAwait(false);
            changes.Add(new SessionChangeEvent(sessionId, SessionChangeKind.Closed, SessionStatus.Closed, now, latestEvent));
        }

        return changes;
    }

    /// <summary>
    /// Tails <paramref name="sessionId"/>'s <c>events.jsonl</c> from where the last call left off
    /// and returns the most recent event read, if any — pure enrichment detail for
    /// <see cref="SessionChangeEvent.LatestEvent"/>, never used to decide <see cref="SessionStatus"/>
    /// itself (see the class doc comment's "signal authority" note). Swallows read failures (e.g.
    /// the file being momentarily unavailable) since missing enrichment on one poll is harmless —
    /// the caller still gets a correct status from the registry/process-liveness check either way.
    /// </summary>
    private async Task<SessionEventRecord?> TryReadLatestEventAsync(string sessionId, CancellationToken cancellationToken)
    {
        var fromOffset = _eventOffsets.TryGetValue(sessionId, out var offset) ? offset : 0;
        try
        {
            var result = await _eventStreamReader.ReadNewEventsAsync(sessionId, fromOffset, cancellationToken).ConfigureAwait(false);
            _eventOffsets[sessionId] = result.NextByteOffset;
            return result.Events.Count > 0 ? result.Events[^1] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>This engine's own per-session bookkeeping between polls — just the last known <see cref="OpenSessionEntry.Working"/> flag.</summary>
    private sealed record TrackedSession(bool WasWorking);
}
