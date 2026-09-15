namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A row from the <c>checkpoints</c> table in <c>session-store.db</c>.
/// </summary>
/// <param name="SessionId">The owning session's id.</param>
/// <param name="CheckpointNumber">The checkpoint's sequence number within the session.</param>
/// <param name="Title">Short checkpoint title, if any.</param>
/// <param name="Overview">Longer checkpoint overview/summary, if any.</param>
/// <param name="CreatedAtUtc">When this checkpoint was recorded.</param>
public sealed record SessionCheckpoint(
    string SessionId,
    int CheckpointNumber,
    string? Title,
    string? Overview,
    DateTimeOffset CreatedAtUtc);
