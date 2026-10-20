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
    private void ToggleSticker(SessionItemViewModel? session)
    {
        if (session is null || !IsShowingLiveData)
        {
            return;
        }

        if (session.HasSticker && !session.IsStickerHidden)
        {
            SetSticker(session, false);
            return;
        }

        if (session.HasSticker)
        {
            session.IsStickerHidden = false;
        }
        else
        {
            SetSticker(session, true);
        }

        if (!StickersVisible)
        {
            StickersVisible = true;
        }
    }

    private void SetSticker(SessionItemViewModel session, bool sticker)
    {
        _stickerState.SetPinned(session.Id, sticker);
        session.HasSticker = sticker;
    }

    private async Task LoadStickerStateAsync()
    {
        await _stickerState.LoadAsync();
        StickersVisible = _stickerState.Visible;
        StickerOpacity = _stickerState.Opacity;
    }

    private static void ApplyGist(SessionItemViewModel row, SessionChangeEvent change)
    {
        if (SessionGist.Choose(change.Status, change.Request, change.Outcome) is { } gist)
        {
            row.Gist = gist;
        }
    }
}
