using System.Windows;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App;

/// <summary>
/// Small input box shown by "🏷 Add label" / "🏷 Edit label": one line of text for the label of a
/// session. On "Save", <see cref="Window.DialogResult"/> is <see langword="true"/> and
/// <see cref="Label"/> holds what was typed, which is empty when the label is to be removed.
/// </summary>
public partial class SessionLabelWindow : Window
{
    public SessionLabelWindow(string sessionName, string? currentLabel)
    {
        InitializeComponent();
        SessionNameText.Text = sessionName;
        LabelBox.MaxLength = SessionLabels.MaxLength;
        LabelBox.Text = currentLabel ?? string.Empty;
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    /// <summary>What was typed, valid only when <see cref="Window.DialogResult"/> is <see langword="true"/>.</summary>
    public string Label { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Label = LabelBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
