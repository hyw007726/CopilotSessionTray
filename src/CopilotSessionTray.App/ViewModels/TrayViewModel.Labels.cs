using System.IO;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.ViewModels;

public sealed partial class TrayViewModel
{
    private readonly SessionLabels _sessionLabels;

    [RelayCommand]
    private async Task EditLabel(SessionItemViewModel? session)
    {
        if (session is null || !IsShowingLiveData)
        {
            return;
        }

        var dialog = new SessionLabelWindow(session.DisplayName, session.Label) { Owner = TryGetOwnerWindow() };
        if (dialog.ShowDialog() == true)
        {
            await SetLabelAsync(session, dialog.Label);
        }
    }

    /// <summary>Gives a session a label, changes it, or removes it when <paramref name="text"/> is empty, and saves the result.</summary>
    /// <param name="session">The row to label.</param>
    /// <param name="text">The label as typed.</param>
    public async Task SetLabelAsync(SessionItemViewModel session, string? text)
    {
        try
        {
            await _sessionLabels.SetAsync(session.Id, text);
        }
        finally
        {
            session.ApplyLabel(_sessionLabels.Get(session.Id));
        }
    }

    private async Task LoadLabelsAsync()
    {
        try
        {
            await _sessionLabels.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
