using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.ViewModels;

public sealed partial class TrayViewModel
{
    private readonly SessionPins _sessionPins;

    /// <summary><see cref="Sessions"/> as the list shows it: pinned rows first, every other row in its usual order.</summary>
    public ICollectionView SessionsView { get; }

    private static ICollectionView CreateSessionsView(ObservableCollection<SessionItemViewModel> sessions)
    {
        var view = new ListCollectionView(sessions) { CustomSort = new PinnedFirstComparer(sessions) };
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(SessionItemViewModel.IsPinned));
        return view;
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
