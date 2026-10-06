using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CopilotSessionTray.App.ViewModels;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotSessionTray.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // No "Global\" prefix: a personal, single-user tool only ever needs to be unique within this
    // one interactive logon session, not across every user on the machine.
    private const string SingleInstanceMutexName = "CopilotSessionTray-SingleInstance-Mutex";

    /// <summary>
    /// Hard ceiling on consecutive dispatcher-exception dialogs (IMPLEMENTATION_PLAN.md §9.5) —
    /// a real crash loop (a recurring GDI+ race in H.NotifyIcon; see IMPLEMENTATION_PLAN.md
    /// §9.5/§9.6 and <c>MainWindow.xaml.cs</c>'s own tray-icon doc comments) showed this handler
    /// can itself become the problem: each recurrence gets
    /// "handled" and the app survives, but a new dialog pops up again and again faster than a
    /// user can dismiss them, making the app feel completely unresponsive/unclosable even though
    /// it's technically still running. Once this many fire with no clean gap between them (see
    /// <see cref="_lastExceptionDialogAt"/>), this handler stops showing any more and shuts the
    /// app down outright — closable-but-crashed beats stuck-in-an-endless-popup-loop.
    /// </summary>
    private const int MaxConsecutiveExceptionDialogs = 3;

    /// <summary>Recurrence window (IMPLEMENTATION_PLAN.md §9.5): dialogs more than this far apart don't count toward <see cref="MaxConsecutiveExceptionDialogs"/> — only a tight, repeating failure loop should trigger a forced shutdown.</summary>
    private static readonly TimeSpan ExceptionDialogRecurrenceWindow = TimeSpan.FromSeconds(5);

    private MainWindow? _mainWindow;
    private ServiceProvider? _serviceProvider;
    private Mutex? _singleInstanceMutex;
    private bool _isPrimaryInstance;
    private int _consecutiveExceptionDialogCount;
    private DateTime _lastExceptionDialogAt = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard (IMPLEMENTATION_PLAN.md §9.1/§9.3's still-open gap, closed here) —
        // checked first, before any other startup work, so a duplicate launch (e.g. a manual
        // double-click while the Scheduled Task's own copy is already running) does the least
        // possible work before bailing out. Without this, two processes would each show a tray
        // icon, independently poll/notify, and race on app-state.json (IAppStateStore's
        // SemaphoreSlim only serializes writes *within* one process, not across two).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);
        _isPrimaryInstance = createdNew;
        if (!_isPrimaryInstance)
        {
            MessageBox.Show(
                "Copilot Session Tray is already running — check your system tray icon.",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Safety net for a tray-resident app: an unhandled exception on the dispatcher thread
        // (e.g. from a routed event handler or a command) otherwise propagates all the way out of
        // Application.Run() and silently kills the whole process with no error shown at all —
        // exactly what happened before this was added (see TrayViewModel.TryGetOwnerWindow's doc
        // comment for the real bug that first exposed this). Showing what broke, instead of just
        // vanishing, is worth it even though this only catches exceptions on this one thread.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // DI composition root — added 2026-09-29 per IMPLEMENTATION_PLAN.md §9.1 ("no DI
        // container" gap): everything below used to be `new OpenSessionsRegistryReader()` etc.
        // field initializers directly inside TrayViewModel. All singletons: there's exactly one
        // tray icon/ViewModel/window for this app's entire lifetime, same as before — this only
        // changes *where* the object graph is built, not how long anything lives.
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Construct the window that owns the TaskbarIcon, but never Show()
        // it — ForceCreate() makes the Win32 tray icon appear regardless.
        // The window itself only becomes visible as the popup when the
        // user clicks the tray icon (see MainWindow.xaml.cs).
        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        _mainWindow.InitializeTrayIcon();

        // Starts the continuous background monitor (§10 Phase 3) — real Started/WorkingStateChanged/
        // Finished/Closed detection + notifications, running for the app's entire lifetime from
        // here on regardless of what's currently displayed. Started only after the tray icon
        // exists so NotificationService.AttachTrayIcon (called by InitializeTrayIcon above) has
        // already run by the time the first poll could possibly emit a notification.
        _serviceProvider.GetRequiredService<TrayViewModel>().StartMonitoring();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IOpenSessionsRegistryReader, OpenSessionsRegistryReader>();
        services.AddSingleton<IProcessLivenessChecker, ProcessLivenessChecker>();
        services.AddSingleton<ISessionHistoryStore, SessionHistoryStore>();
        // Deliberately concrete, not behind an interface — see SessionLockFileInspector's own doc
        // comment for why it isn't one of the reviewed Core contracts.
        services.AddSingleton<SessionLockFileInspector>();
        services.AddSingleton<ISessionLauncher, SessionLauncher>();
        services.AddSingleton<IYoloTaskRunner, YoloTaskRunner>();
        services.AddSingleton<IAppStateStore, AppStateStore>();
        services.AddSingleton<ISessionEventStreamReader, SessionEventStreamReader>();
        services.AddSingleton<ISessionDetectionEngine, SessionDetectionEngine>();

        // Registered as its own concrete singleton (not just behind INotificationService) so
        // MainWindow can also depend on it directly to call AttachTrayIcon — a method deliberately
        // kept off INotificationService itself since it's WPF/H.NotifyIcon-specific plumbing, not
        // part of the reviewed Core-facing contract. TrayViewModel still only ever sees it as
        // INotificationService, resolving to this exact same instance.
        services.AddSingleton<Services.NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<Services.NotificationService>());

        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var now = DateTime.UtcNow;
        _consecutiveExceptionDialogCount = (now - _lastExceptionDialogAt) <= ExceptionDialogRecurrenceWindow
            ? _consecutiveExceptionDialogCount + 1
            : 1;
        _lastExceptionDialogAt = now;

        if (_consecutiveExceptionDialogCount > MaxConsecutiveExceptionDialogs)
        {
            // A tight, repeating failure loop (see MaxConsecutiveExceptionDialogs's doc comment)
            // — showing yet another dialog would just add to the pile the user can't get out of.
            // e.Handled is deliberately left false here: that lets this exception finish
            // terminating the process via the normal unhandled-exception path (not a graceful
            // Shutdown(), since whatever's wrong may already have the dispatcher/UI in a bad
            // state) rather than risking another suppressed-but-still-broken cycle.
            return;
        }

        MessageBox.Show(
            $"An unexpected error occurred and was suppressed so the app could keep running:{Environment.NewLine}{Environment.NewLine}{e.Exception}",
            "Copilot Session Tray — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.ShutdownTrayIcon();
        _serviceProvider?.Dispose();

        // Only release if this instance actually owns it — the early-return duplicate-instance
        // path above also reaches OnExit (via Shutdown()) holding a *handle* to the same named
        // mutex, but never acquired ownership of it (createdNew was false), so calling
        // ReleaseMutex there would throw SynchronizationLockException.
        if (_isPrimaryInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

