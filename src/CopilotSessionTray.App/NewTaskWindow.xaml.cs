using System.Windows;

namespace CopilotSessionTray.App;

/// <summary>
/// "Start a new Copilot task" panel: a prompt box, a workspace folder
/// picker (pre-filled from the last-used workspace, if any), and a
/// permissions checkbox. On "Start", <see cref="DialogResult"/> is
/// <see langword="true"/> and <see cref="Prompt"/>/<see cref="WorkspaceDirectory"/>/
/// <see cref="EnableAllPermissions"/> are populated for the caller
/// (<c>TrayViewModel.StartYoloTask</c>) to launch with.
/// </summary>
public partial class NewTaskWindow : Window
{
    public NewTaskWindow(string? initialWorkspaceDirectory = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialWorkspaceDirectory))
        {
            WorkspaceBox.Text = initialWorkspaceDirectory;
        }
    }

    /// <summary>The prompt entered, valid only when <see cref="Window.DialogResult"/> is <see langword="true"/>.</summary>
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>The workspace folder chosen, valid only when <see cref="Window.DialogResult"/> is <see langword="true"/>.</summary>
    public string WorkspaceDirectory { get; private set; } = string.Empty;

    /// <summary>Whether the "Enable all permissions (--allow-all)" checkbox was checked, valid only when <see cref="Window.DialogResult"/> is <see langword="true"/>.</summary>
    public bool EnableAllPermissions { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a workspace" };
        if (!string.IsNullOrEmpty(WorkspaceBox.Text))
        {
            dialog.InitialDirectory = WorkspaceBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            WorkspaceBox.Text = dialog.FolderName; // triggers Inputs_Changed via WorkspaceBox.TextChanged
        }
    }

    private void Inputs_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateStartButtonEnabled();

    private void UpdateStartButtonEnabled()
    {
        // Guard: this runs before StartButton exists yet, since PromptBox.TextChanged can fire
        // during InitializeComponent().
        if (StartButton is null)
        {
            return;
        }

        StartButton.IsEnabled =
            !string.IsNullOrWhiteSpace(PromptBox.Text) &&
            !string.IsNullOrWhiteSpace(WorkspaceBox.Text);
    }

    private void AllowAllPermissionsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Guard: Checked/Unchecked can fire during InitializeComponent(), before WarningText exists.
        if (WarningText is null)
        {
            return;
        }

        if (AllowAllPermissionsCheckBox.IsChecked == true)
        {
            WarningText.Text = "Runs fully unattended: no per-action confirmation for tools/files/URLs, and the workspace folder is trusted automatically too. Only point this at a workspace and prompt you trust.";
            WarningText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        else
        {
            WarningText.Text = "Copilot will ask for confirmation before running tools, editing files, or accessing URLs — the normal, safer default.";
            WarningText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Prompt = PromptBox.Text;
        WorkspaceDirectory = WorkspaceBox.Text;
        EnableAllPermissions = AllowAllPermissionsCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
