using System.Windows;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// Small dialog shown by "🗒 Summary": displays a session's AI-written
/// summary and lets the user set a local display-name override for it.
/// Binds directly to the row's own <see cref="SessionItemViewModel"/> (no
/// separate view model) — renaming here immediately updates the same
/// instance shown in the main list, since it's the same object.
/// </summary>
public partial class SessionSummaryWindow : Window
{
    public SessionSummaryWindow(SessionItemViewModel session)
    {
        InitializeComponent();
        DataContext = session;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
