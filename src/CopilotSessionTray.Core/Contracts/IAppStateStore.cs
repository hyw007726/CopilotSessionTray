using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Persists this application's own state — read/unread markers and user
/// preferences — entirely separate from Copilot CLI's <c>.copilot</c>
/// folder (typically under <c>%LOCALAPPDATA%\CopilotSessionTray\</c>).
/// Copilot CLI has no native "unread" concept; this app owns it end to end.
/// </summary>
public interface IAppStateStore
{
    /// <summary>Gets the last acknowledgement marker for a session, or null if it has never been acknowledged.</summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<SessionReadMarker?> GetReadMarkerAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Saves (creates or updates) the acknowledgement marker for a session.</summary>
    /// <param name="marker">The marker to persist.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task SaveReadMarkerAsync(SessionReadMarker marker, CancellationToken cancellationToken = default);

    /// <summary>Gets the current notification preferences, or defaults if none have been saved yet.</summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves updated notification preferences (e.g. from the settings panel).</summary>
    /// <param name="preferences">The preferences to persist.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task SavePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default);
}
