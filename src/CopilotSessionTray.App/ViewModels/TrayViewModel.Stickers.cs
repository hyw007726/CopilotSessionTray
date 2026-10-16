using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.ViewModels;

public sealed partial class TrayViewModel
{
    private readonly StickerState _stickerState;

    [ObservableProperty]
    private bool _stickersVisible = true;

    [ObservableProperty]
    private double _stickerOpacity = StickerSettings.DefaultOpacity;

    public double MinStickerOpacity => StickerSettings.MinOpacity;

    public double MaxStickerOpacity => StickerSettings.MaxOpacity;

    partial void OnStickersVisibleChanged(bool value) => _stickerState.SetVisible(value);

    partial void OnStickerOpacityChanged(double value) => _stickerState.SetOpacity(value);

    [RelayCommand]
    private void TogglePin(SessionItemViewModel? session)
    {
        if (session is null || !IsShowingLiveData)
        {
            return;
        }

        var pin = !session.IsPinned;
        SetPinned(session, pin);
        if (pin && !StickersVisible)
        {
            StickersVisible = true;
        }
    }

    private void SetPinned(SessionItemViewModel session, bool pinned)
    {
        _stickerState.SetPinned(session.Id, pinned);
        session.IsPinned = pinned;
    }

    private async Task LoadStickerStateAsync()
    {
        await _stickerState.LoadAsync();
        StickersVisible = _stickerState.Visible;
        StickerOpacity = _stickerState.Opacity;
    }

    private async Task AddMissingPinnedSessionsAsync(DateTimeOffset now)
    {
        foreach (var pinned in _stickerState.Pinned)
        {
            if (Sessions.Any(session => session.Id == pinned.SessionId))
            {
                continue;
            }

            SessionSummary? summary = null;
            try
            {
                summary = await _liveHistoryStore.GetSessionAsync(pinned.SessionId);
            }
            catch
            {
                continue;
            }

            if (summary is not null)
            {
                AddSessionIfNew(await BuildClosedSessionItemAsync(summary, now));
            }
        }
    }

    private static void ApplyGist(SessionItemViewModel row, SessionChangeEvent change)
    {
        if (SessionGist.Choose(change.Status, change.Request, change.Outcome) is { } gist)
        {
            row.Gist = gist;
        }
    }
}
