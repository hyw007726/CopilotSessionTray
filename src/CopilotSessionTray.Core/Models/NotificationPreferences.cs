namespace CopilotSessionTray.Core.Models;

/// <summary>
/// User-configurable notification preferences, persisted by
/// <see cref="Contracts.IAppStateStore"/>. Backs the future settings panel.
/// </summary>
/// <param name="IsMuted">When true, suppress all notifications regardless of other settings.</param>
/// <param name="QuietHoursStart">Start of a daily do-not-disturb window, if set.</param>
/// <param name="QuietHoursEnd">End of a daily do-not-disturb window, if set.</param>
/// <param name="WatchedRepositories">
/// If non-empty, only sessions whose repository/cwd matches an entry here
/// raise notifications. An empty list means "watch everything".
/// </param>
/// <param name="LastNewTaskWorkspaceDirectory">
/// The workspace folder the user last picked in the "Start new task" panel,
/// so it can be pre-filled next time. Not really a <em>notification</em>
/// setting, but reusing this bag rather than adding a new
/// <see cref="Contracts.IAppStateStore"/> method for a single small value —
/// that would grow the Phase 0.5-reviewed interface surface unilaterally.
/// </param>
public sealed record NotificationPreferences(
    bool IsMuted,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    IReadOnlyList<string> WatchedRepositories,
    string? LastNewTaskWorkspaceDirectory = null);
