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
    private MainWindow? _mainWindow;
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
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
        base.OnExit(e);
    }
}

