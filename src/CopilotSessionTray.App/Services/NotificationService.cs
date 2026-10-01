using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Real implementation of <see cref="INotificationService"/> — shows a classic tray balloon
/// notification via <see cref="TaskbarIcon.ShowNotification"/> (<c>H.NotifyIcon</c>). Lives in
/// the App project, not <c>Core</c>: unlike every other real Core service, this one's entire job
/// requires a live WPF <see cref="TaskbarIcon"/> control instance, which Core has no reference to
/// (and shouldn't — it stays UI-agnostic per the project's layering).
/// </summary>
/// <remarks>
/// Chosen over a richer Windows App notification (action buttons, Action Center history) per the
/// 2026-09-29 design-review pass (IMPLEMENTATION_PLAN.md §9.1/§10 Phase 3): no AUMID/Start-Menu-shortcut
/// registration needed, so the app's xcopy/zip distribution model (§11) is untouched. Revisit if
/// that richer experience is wanted later.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private TaskbarIcon? _trayIcon;

    /// <summary>
    /// Supplies the live <see cref="TaskbarIcon"/> to show notifications through, once it exists.
    /// Not a constructor parameter: <see cref="TrayViewModel"/> (which needs this service) and
    /// <c>MainWindow</c> (which owns the <see cref="TaskbarIcon"/>) are both resolved from the
    /// same DI container, and <c>MainWindow</c> already depends on <see cref="TrayViewModel"/> for
    /// its <c>DataContext</c> — taking a <see cref="TaskbarIcon"/>/<c>MainWindow</c> dependency
    /// here too would create a circular constructor dependency. <c>MainWindow.InitializeTrayIcon()</c>
    /// calls this once, right after <c>TaskbarIcon.ForceCreate()</c>.
    /// </summary>
    public void AttachTrayIcon(TaskbarIcon trayIcon) => _trayIcon = trayIcon;

    public Task NotifySessionFinishedAsync(SessionNotification notification, CancellationToken cancellationToken = default)
    {
        // No tray icon attached yet (e.g. a poll landed before startup finished) — silently skip
        // rather than throw; missing one notification is far less harmful than crashing the poll
        // loop over it.
        _trayIcon?.ShowNotification(notification.Title, notification.Body, NotificationIcon.Info);
        return Task.CompletedTask;
    }
}
