using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.ViewModels;

public sealed partial class TrayViewModel
{
    private readonly SessionPins _sessionPins;
    private readonly SemaphoreSlim _showPinnedOnlySaveGate = new(1, 1);

    /// <summary>
    /// <see cref="Sessions"/> as the list shows it: pinned rows first, every other row in its usual
    /// order, and only the pinned rows while <see cref="ShowPinnedOnly"/> is on.
    /// </summary>
    public ICollectionView SessionsView { get; }

    /// <summary>
    /// Whether the popup lists only the pinned sessions. It filters the list and nothing else: the
    /// tray icon, its tooltip and the stickers still count every session, so one that needs you
    /// turns the tray icon red even while its row is hidden. Saved with the other preferences.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private bool _showPinnedOnly;

    private static ICollectionView CreateSessionsView(ObservableCollection<SessionItemViewModel> sessions)
    {
        var view = new ListCollectionView(sessions) { CustomSort = new PinnedFirstComparer(sessions) };
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(SessionItemViewModel.IsPinned));
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(SessionItemViewModel.IsPinned));
        return view;
    }

    partial void OnShowPinnedOnlyChanged(bool value)
    {
        SessionsView.Filter = value ? IsPinnedRow : null;
        _ = SaveShowPinnedOnlyAsync();
    }

    private static bool IsPinnedRow(object row) => ((SessionItemViewModel)row).IsPinned;

    /// <summary>
    /// Saves <see cref="ShowPinnedOnly"/> with the other preferences. Saves run one at a time and
    /// each writes the value as it is by then, so a quick run of changes always ends with the last
    /// one saved; nothing is written when the file already says the same, which is the case
    /// when the value was only just read from it at start-up.
    /// </summary>
    private async Task SaveShowPinnedOnlyAsync()
    {
        await _showPinnedOnlySaveGate.WaitAsync();
        try
        {
            var showPinnedOnly = ShowPinnedOnly;
            var preferences = await _appStateStore.GetPreferencesAsync();
            if (preferences.ShowPinnedOnly != showPinnedOnly)
            {
                await _appStateStore.SavePreferencesAsync(preferences with { ShowPinnedOnly = showPinnedOnly });
            }
        }
        finally
        {
            _showPinnedOnlySaveGate.Release();
        }
    }

    [RelayCommand]
    private async Task TogglePin(SessionItemViewModel? session)
    {
        if (session is null || !IsShowingLiveData)
        {
            return;
        }

        var pinned = !session.IsPinned;
        session.IsPinned = pinned;
        await _sessionPins.SetPinnedAsync(session.Id, pinned);
    }

    private Task LoadPinsAsync() => _sessionPins.LoadAsync(_stickerState.Pinned.Select(item => item.SessionId));

    private bool IsKept(string sessionId) => _sessionPins.IsPinned(sessionId) || _stickerState.IsPinned(sessionId);

    private async Task AddMissingPinnedSessionsAsync(DateTimeOffset now)
    {
        var missing = _sessionPins.Ids
            .Concat(_stickerState.Pinned.Select(item => item.SessionId))
            .Distinct()
            .Where(id => Sessions.All(session => session.Id != id))
            .ToList();

        var found = new List<SessionSummary>();
        foreach (var id in missing)
        {
            SessionSummary? summary;
            try
            {
                summary = await _liveHistoryStore.GetSessionAsync(id);
            }
            catch
            {
                continue;
            }

            if (summary is not null)
            {
                found.Add(summary);
            }
        }

        foreach (var summary in found.OrderByDescending(item => item.UpdatedAtUtc))
        {
            AddSessionIfNew(await BuildClosedSessionItemAsync(summary, now));
        }
    }

    private sealed class PinnedFirstComparer(IList<SessionItemViewModel> source) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (SessionItemViewModel)x!;
            var right = (SessionItemViewModel)y!;
            var byPin = right.IsPinned.CompareTo(left.IsPinned);
            return byPin != 0 ? byPin : source.IndexOf(left).CompareTo(source.IndexOf(right));
        }
    }
}
