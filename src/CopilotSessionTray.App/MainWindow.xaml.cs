using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e) => TogglePopup();

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
    /// Opens a small choice menu for the "⤴ Resume" button — done in
    /// code-behind (not command/XAML bindings like the other row buttons)
    /// because a ContextMenu is its own popup root, not part of the normal
    /// visual tree, which makes the ElementName-back-to-RootWindow pattern
    /// used elsewhere in this row unreliable here.
    /// </summary>
    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not SessionItemViewModel session ||
            DataContext is not TrayViewModel viewModel)
        {
            return;
        }

        var menu = new ContextMenu();

        var historyItem = new MenuItem { Header = "Resume with history" };
        historyItem.Click += (_, _) => viewModel.ResumeSessionCommand.Execute(session);
        menu.Items.Add(historyItem);

        var summaryItem = new MenuItem { Header = "Resume with summary" };
        summaryItem.Click += (_, _) => viewModel.StartSessionFromSummaryCommand.Execute(session);
        menu.Items.Add(summaryItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cancel" }); // no handler: selecting it just dismisses the menu.

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
