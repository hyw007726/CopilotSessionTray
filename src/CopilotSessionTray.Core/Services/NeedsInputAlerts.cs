using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Decides when the user should be alerted that a session is blocked on them, from the stream of
/// <see cref="SessionChangeEvent"/>s the detection engine emits. A session is alerted about once
/// per wait: when it goes from not waiting to waiting. A wait that was already under way before
/// monitoring began (a prompt left unanswered from earlier, replayed from the log on the first
/// poll) is remembered but never alerted about, so starting the app does not ring for old
/// prompts. Not thread-safe: feed it from one place, in order.
/// </summary>
public sealed class NeedsInputAlerts
{
    private readonly DateTimeOffset _monitoringStartedUtc;
    private readonly HashSet<string> _waiting = new();

    /// <param name="monitoringStartedUtc">When monitoring began; prompts raised before this are not alerted.</param>
    public NeedsInputAlerts(DateTimeOffset monitoringStartedUtc)
    {
        _monitoringStartedUtc = monitoringStartedUtc;
    }

    /// <summary>Takes one change in and says what, if anything, the user should now be told.</summary>
    /// <param name="change">The change, in the order the engine emitted it.</param>
    public NeedsInputAction Observe(SessionChangeEvent change)
    {
        var prompt = change.Kind == SessionChangeKind.Closed ? default : change.Prompt;
        var wasWaiting = _waiting.Contains(change.SessionId);

        if (!prompt.IsPending)
        {
            _waiting.Remove(change.SessionId);
            return wasWaiting ? NeedsInputAction.Dismiss : NeedsInputAction.None;
        }

        _waiting.Add(change.SessionId);
        if (wasWaiting)
        {
            return NeedsInputAction.None;
        }

        var raisedAt = prompt.SinceUtc ?? change.OccurredAtUtc;
        return raisedAt >= _monitoringStartedUtc ? NeedsInputAction.Alert : NeedsInputAction.None;
    }
}
