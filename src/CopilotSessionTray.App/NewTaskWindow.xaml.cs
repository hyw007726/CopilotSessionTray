using System.Windows;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App;

/// <summary>
/// "Start a new Copilot task" panel: a prompt box, a workspace folder
/// picker (pre-filled from the last-used workspace, if any), and a
/// permissions checkbox. It is an ordinary window, neither modal nor always on top, so it goes
/// behind whatever you click and the rest of the tray keeps working while it is open; it opens in
/// the bottom-right corner next to the tray, where the popup does. On "Start",
/// <see cref="Started"/> is <see langword="true"/> and <see cref="Prompt"/>/<see cref="WorkspaceDirectory"/>/
/// <see cref="EnableAllPermissions"/> are populated for the caller
/// (<c>TrayViewModel.StartYoloTask</c>) to launch with. Everything typed is kept in the
/// <see cref="NewTaskDraftState"/> as it changes and put back when the panel opens again.
/// </summary>
public partial class NewTaskWindow : Window
{
    private readonly NewTaskDraftState _draft;
    private readonly bool _restoredDraft;
    private bool _initializing = true;

    public NewTaskWindow(string? initialWorkspaceDirectory, NewTaskDraftState draft)
    {
        _draft = draft;
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;

        var restored = draft.Current;
        _restoredDraft = restored is not null;
        var workspace = string.IsNullOrEmpty(restored?.WorkspaceDirectory) ? initialWorkspaceDirectory : restored.WorkspaceDirectory;
        if (!string.IsNullOrEmpty(workspace))
        {
            WorkspaceBox.Text = workspace;
        }

        if (restored is not null)
        {
            PromptBox.Text = restored.Prompt;
            PromptBox.CaretIndex = restored.Prompt.Length;
            AllowAllPermissionsCheckBox.IsChecked = restored.EnableAllPermissions;
        }

        _initializing = false;
        UpdateDraftNote();
    }

    /// <summary>Whether the panel was closed with "Start" rather than cancelled or closed.</summary>
    public bool Started { get; private set; }

    /// <summary>The prompt entered, valid only when <see cref="Started"/> is <see langword="true"/>.</summary>
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>The workspace folder chosen, valid only when <see cref="Started"/> is <see langword="true"/>.</summary>
    public string WorkspaceDirectory { get; private set; } = string.Empty;

    /// <summary>Whether the "Enable all permissions (--allow-all)" checkbox was checked, valid only when <see cref="Started"/> is <see langword="true"/>.</summary>
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

    private void Inputs_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateStartButtonEnabled();
        UpdateDraftNote();
        SaveDraft();
    }

    private void SaveDraft()
    {
        if (!_initializing)
        {
            _draft.Save(PromptBox.Text, WorkspaceBox.Text, AllowAllPermissionsCheckBox.IsChecked == true);
        }
    }

    private void UpdateDraftNote()
    {
        if (!_initializing)
        {
            DraftNote.Visibility = _restoredDraft && !string.IsNullOrWhiteSpace(PromptBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

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

        SaveDraft();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Prompt = PromptBox.Text;
        WorkspaceDirectory = WorkspaceBox.Text;
        EnableAllPermissions = AllowAllPermissionsCheckBox.IsChecked == true;
        Started = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
