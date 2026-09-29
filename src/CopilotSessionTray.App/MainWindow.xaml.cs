using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // Windows sends a left-mouse-up for *each* click of a double-click, in addition to the
    // distinct double-click message — confirmed via H.NotifyIcon's own source (it just relays the
    // underlying WM_LBUTTONUP/WM_LBUTTONDBLCLK sequence): TrayLeftMouseUp fires twice before
    // TrayLeftMouseDoubleClick also fires. Debounce with a short timer, standard practice for
    // this exact tray-icon ambiguity, so a double-click doesn't also toggle the popup open/shut
    // right before the "start new task" window appears on top of it.
    private readonly DispatcherTimer _singleClickTimer;

    public MainWindow(TrayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            TogglePopup();
        };
    }

    /// <summary>
    /// Forces the Win32 tray icon to exist even though this window is never
    /// shown at startup. Called once from <c>App.OnStartup</c>.
    /// </summary>
    public void InitializeTrayIcon() => TrayIcon.ForceCreate();

    /// <summary>Releases the tray icon's native resources on app shutdown.</summary>
    public void ShutdownTrayIcon() => TrayIcon.Dispose();

    protected override void OnClosing(CancelEventArgs e)
    {
        // This is a tray-only app: closing the popup (its ToolWindow "X",
        // or Alt+F4) should just hide it, not exit the whole app. The only
        // real exit path is the tray context menu's "Quit" command.
        e.Cancel = true;
        Hide();
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void TrayIcon_TrayLeftMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        _singleClickTimer.Stop(); // cancel the pending single-click popup toggle.
        if (DataContext is TrayViewModel viewModel)
        {
            viewModel.StartYoloTaskCommand.Execute(null);
        }
    }

    private void ShowSessions_Click(object sender, RoutedEventArgs e) => TogglePopup(forceShow: true);

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void TogglePopup(bool forceShow = false)
    {
        if (IsVisible && !forceShow)
        {
            Hide();
            return;
        }

        PositionNearTray();
        Show();
        Activate();
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;
    }

    /// <summary>
    /// Opens a small choice menu for the "⤴ Resume" button — see <see cref="ResumeMenuHelper"/>.
    /// </summary>
    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.DataContext is SessionItemViewModel session &&
            DataContext is TrayViewModel viewModel)
        {
            ResumeMenuHelper.Show(button, session, viewModel);
        }
    }
}
