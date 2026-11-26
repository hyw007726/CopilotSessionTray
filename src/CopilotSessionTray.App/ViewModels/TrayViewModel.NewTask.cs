using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.ViewModels;

public sealed partial class TrayViewModel
{
    private readonly NewTaskDraftState _newTaskDraft;
    private NewTaskWindow? _newTaskWindow;
    private bool _isOpeningNewTask;

    /// <summary>
    /// Opens the "new task" panel (prompt + workspace picker, pre-filled from the last-used
    /// workspace via <see cref="IAppStateStore"/>), then starts it via
    /// <see cref="IYoloTaskRunner.StartInteractiveAsync"/> on "Start". The panel is an ordinary
    /// window (not modal, not owned by the popup, not always on top), and there is only ever one:
    /// asking for it while it is open brings that one to the front. What was typed is kept as a
    /// draft (<see cref="NewTaskDraftState"/>) when the panel is closed without starting, and is
    /// put back the next time it opens; starting a task uses it up, unless the launch fails.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartYoloTask()
    {
        if (_newTaskWindow is { } open)
        {
            BringToFront(open);
            return;
        }

        if (_isOpeningNewTask)
        {
            return;
        }

        NewTaskWindow window;
        var closed = new TaskCompletionSource();
        _isOpeningNewTask = true;
        try
        {
            var lastWorkspace = (await _appStateStore.GetPreferencesAsync()).LastNewTaskWorkspaceDirectory;
            await LoadNewTaskDraftAsync();
            window = new NewTaskWindow(lastWorkspace, _newTaskDraft);
            window.Closed += (_, _) =>
            {
                _newTaskWindow = null;
                closed.SetResult();
            };
            window.Show();
            _newTaskWindow = window;
        }
        finally
        {
            _isOpeningNewTask = false;
        }

        BringToFront(window);
        await closed.Task;

        if (window.Started)
        {
            _newTaskDraft.Clear();
        }

        await _newTaskDraft.FlushAsync();
        if (!window.Started)
        {
            return;
        }

        var launched = false;
        try
        {
            var preferences = await _appStateStore.GetPreferencesAsync();
            await _appStateStore.SavePreferencesAsync(preferences with { LastNewTaskWorkspaceDirectory = window.WorkspaceDirectory });

            launched = await TryLaunchAsync(
                () => _yoloTaskRunner.StartInteractiveAsync(window.Prompt, window.WorkspaceDirectory, window.EnableAllPermissions),
                "new task");
        }
        finally
        {
            if (!launched)
            {
                _newTaskDraft.Save(window.Prompt, window.WorkspaceDirectory, window.EnableAllPermissions);
            }
        }
    }

    private async Task LoadNewTaskDraftAsync()
    {
        try
        {
            await _newTaskDraft.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }
}
