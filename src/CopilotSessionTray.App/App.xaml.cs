using System.Windows;
using System.Windows.Threading;

namespace CopilotSessionTray.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;

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

        // Construct the window that owns the TaskbarIcon, but never Show()
        // it — ForceCreate() makes the Win32 tray icon appear regardless.
        // The window itself only becomes visible as the popup when the
        // user clicks the tray icon (see MainWindow.xaml.cs).
        _mainWindow = new MainWindow();
        _mainWindow.InitializeTrayIcon();
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
        base.OnExit(e);
    }
}

