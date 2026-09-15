using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Dispatches a user-facing notification (e.g. a Windows toast) for a
/// session that finished while unattended. Kept as an interface so it is
/// fakeable in tests and swappable if the notification mechanism changes.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a notification for a finished session.</summary>
    /// <param name="notification">The notification content to display.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task NotifySessionFinishedAsync(SessionNotification notification, CancellationToken cancellationToken = default);
}
