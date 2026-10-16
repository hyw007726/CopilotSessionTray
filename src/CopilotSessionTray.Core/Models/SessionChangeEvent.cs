namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A single state transition emitted by
/// <see cref="Contracts.ISessionDetectionEngine.PollAsync"/>.
/// </summary>
/// <param name="SessionId">The affected session.</param>
/// <param name="Kind">What kind of transition this is.</param>
/// <param name="Status">The session's resulting status after this transition.</param>
/// <param name="OccurredAtUtc">When the detection engine observed this transition (not necessarily when it happened on disk).</param>
/// <param name="LatestEvent">The most recent parsed event that triggered/accompanies this transition, if any.</param>
/// <param name="Outcome">
/// For <see cref="SessionChangeKind.Finished"/>: what the session reported when it finished — its
/// <c>task_complete</c> summary or, failing that, its last assistant message — if one was seen.
/// </param>
/// <param name="WorkedFor">
/// For <see cref="SessionChangeKind.Finished"/>: how long the session worked, measured from the
/// user's last message to the last event seen, if both are known.
/// </param>
public sealed record SessionChangeEvent(
    string SessionId,
    SessionChangeKind Kind,
    SessionStatus Status,
    DateTimeOffset OccurredAtUtc,
    SessionEventRecord? LatestEvent,
    string? Outcome = null,
    TimeSpan? WorkedFor = null);
