using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Dispatches a user-facing notification (e.g. a Windows toast) for a
/// session that finished while unattended, or is waiting on the user. Kept
/// as an interface so it is fakeable in tests and swappable if the
/// notification mechanism changes.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a notification for a finished session.</summary>
    /// <param name="notification">The notification content to display.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task NotifySessionFinishedAsync(SessionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows an alert, with a sound, that a session is blocked on the user. The alert stays up
    /// until <see cref="DismissNeedsInputAsync"/> takes it down or the user dismisses it.
    /// </summary>
    /// <param name="notification">The notification content to display.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task NotifySessionNeedsInputAsync(SessionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes down the alert <see cref="NotifySessionNeedsInputAsync"/> showed for a session, if
    /// there is one — the user has answered, or the session has gone.
    /// </summary>
    /// <param name="sessionId">The session whose alert to take down.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DismissNeedsInputAsync(string sessionId, CancellationToken cancellationToken = default);
}
