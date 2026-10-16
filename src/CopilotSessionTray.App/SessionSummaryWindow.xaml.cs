using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// Small dialog shown by "🗒 Summary": displays a session's summary as formatted,
/// selectable text, the exact text a new session would be seeded with, and lets
/// the user set a local display-name override for it.
/// Binds directly to the row's own <see cref="SessionItemViewModel"/> (no
/// separate view model) — renaming here immediately updates the same
/// instance shown in the main list, since it's the same object.
/// </summary>
public partial class SessionSummaryWindow : Window
{
    private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public SessionSummaryWindow(SessionItemViewModel session)
    {
        InitializeComponent();
        DataContext = session;

        SourceText.Text = session.SummaryView?.Describe(DateTimeOffset.UtcNow) ?? "Sample data";
        SummaryBox.Document = SummaryDocumentBuilder.Build(session.SummaryView, session.Summary);
        SeedBox.Text = session.Summary;

        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButton.Content = "Copy";
        };
        Loaded += (_, _) => KeepOnScreen();
        Closed += (_, _) => _copyFeedbackTimer.Stop();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        Width = width;
        Height = height;
        Left = Math.Max(area.Left, Math.Min(Left, area.Right - width));
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - height));
    }

    private void CopySeed_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(SeedBox.Text, copy: true);
            CopyButton.Content = "Copied";
        }
        catch (COMException)
        {
            CopyButton.Content = "Couldn't copy";
        }

        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
