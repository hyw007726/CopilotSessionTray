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
public sealed record NotificationPreferences(
    bool IsMuted,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    IReadOnlyList<string> WatchedRepositories);
