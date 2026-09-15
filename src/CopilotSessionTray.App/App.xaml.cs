using System.Windows;

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

        // Construct the window that owns the TaskbarIcon, but never Show()
        // it — ForceCreate() makes the Win32 tray icon appear regardless.
        // The window itself only becomes visible as the popup when the
        // user clicks the tray icon (see MainWindow.xaml.cs).
        _mainWindow = new MainWindow();
        _mainWindow.InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.ShutdownTrayIcon();
        base.OnExit(e);
    }
}

