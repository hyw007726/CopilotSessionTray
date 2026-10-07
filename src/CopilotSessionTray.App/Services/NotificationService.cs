using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using H.NotifyIcon;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Real implementation of <see cref="INotificationService"/> — shows a custom WPF popup via
/// <see cref="TaskbarIcon.ShowCustomBalloon"/> (<c>H.NotifyIcon</c>), not the native OS balloon
/// (<see cref="TaskbarIcon.ShowNotification"/>, used until 2026-10-07 — see this class's own
/// remarks for why that changed). Lives in the App project, not <c>Core</c>: unlike every other
/// real Core service, this one's entire job requires a live WPF <see cref="TaskbarIcon"/> control
/// instance, which Core has no reference to (and shouldn't — it stays UI-agnostic per the
/// project's layering).
/// </summary>
/// <remarks>
/// <para>
/// Originally used the native OS balloon (chosen over a richer Windows App notification — action
/// buttons, Action Center history — per the 2026-09-29 design-review pass, IMPLEMENTATION_PLAN.md
/// §9.1/§10 Phase 3: no AUMID/Start-Menu-shortcut registration needed, so the app's xcopy/zip
/// distribution model, §11, stayed untouched).
/// </para>
/// <para>
/// <b>Changed 2026-10-07</b> (user: "I see there's a small reminder box popped out when a session
/// is complete, but it disappeared very quickly, can we make it stay there?"): the native balloon
/// has no reliable way to do this. Its own <c>timeout</c> parameter is explicitly documented
/// (confirmed from H.NotifyIcon's real source, not assumed) as deprecated since Windows Vista —
/// "Notification display times are now based on system accessibility settings. The system
/// enforces minimum and maximum timeout values... currently set at 10 seconds and 30 seconds."
/// H.NotifyIcon does have an internal mechanism meant to work around this
/// (<c>TaskbarIcon.ResetBalloonCloseTimer()</c>, which re-sends the same native notification every
/// 25 seconds to keep it alive) — tried first, via a real <c>TaskbarIcon</c> with
/// <c>TrayBalloonTipShown</c>/<c>TrayBalloonTipClosed</c> subscribed, to check empirically whether
/// each 25-second refresh would visibly re-pop/flicker the notification. Those specific events
/// never fired at all on this machine's Windows version (for either the initial notification or
/// any refresh) — meaning they don't appear to route through the modern Action-Center-style toast
/// presentation a classic balloon gets converted to on Windows 10/11, so they couldn't actually
/// answer the question. Given that genuine uncertainty, and that getting this wrong would make
/// the user's exact complaint worse (a notification that visibly re-pops every 25 seconds would
/// be more annoying than one that merely vanishes quickly), switched to <c>ShowCustomBalloon</c>
/// instead — a real, plain WPF <see cref="Popup"/> hosting a <see cref="UIElement"/> this class
/// builds and fully controls, with <c>timeout: null</c> ("keep the balloon open indefinitely" per
/// the library's own doc comment) — no refresh trickery needed at all, and no uncertainty about
/// how it behaves, since nothing about its lifetime is mediated by Windows' own notification
/// pipeline.
/// </para>
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
        if (_trayIcon is null)
        {
            return Task.CompletedTask;
        }

        var balloon = BuildBalloonContent(notification);
        balloon.MouseLeftButtonUp += (_, _) => _trayIcon.CloseBalloon();

        // ShowCustomBalloon itself replaces any currently-open custom balloon first (confirmed
        // from source: it unconditionally calls CloseBalloon() before creating the new popup), so
        // a second notification arriving before the first was dismissed naturally supersedes it
        // rather than stacking two balloons - no extra bookkeeping needed here for that case.
        _trayIcon.ShowCustomBalloon(balloon, PopupAnimation.Slide, timeout: null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the visual content of the persistent notification balloon — plain text, no fancy
    /// theming, deliberately close in spirit to what the native balloon used to show (title bold,
    /// body underneath), just hosted in a real, fully app-controlled <see cref="Border"/> instead
    /// of something Windows itself times out.
    /// </summary>
    private static Border BuildBalloonContent(SessionNotification notification)
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock
        {
            Text = notification.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = notification.Body,
            FontSize = 11,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            // "Click to dismiss" (2026-10-07, revised same day — was "Click to view"): this app
            // has no way to actually jump to the session's real, already-open terminal window, so
            // clicking no longer opens the main popup either — dismissing this notification *is*
            // the entire interaction, acknowledging the session is done without claiming to take
            // the user anywhere.
            Text = "Click to dismiss",
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return new Border
        {
            Width = 300,
            Background = Brushes.White,
            BorderBrush = Brushes.Gainsboro,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect { ShadowDepth = 2, BlurRadius = 8, Opacity = 0.3 },
            Child = stack,
        };
    }
}
