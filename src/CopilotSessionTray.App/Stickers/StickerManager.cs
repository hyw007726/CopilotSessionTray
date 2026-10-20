using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CopilotSessionTray.App.ViewModels;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.Stickers;

public sealed class StickerManager : IDisposable
{
    private readonly TrayViewModel _viewModel;
    private readonly StickerState _state;
    private readonly Func<Window, SessionItemViewModel, bool> _confirmResume;
    private readonly Dictionary<string, StickerWindow> _windows = new();
    private readonly Dictionary<string, SessionStatus> _snoozed = new();
    private readonly HashSet<SessionItemViewModel> _watched = new();
    private bool _started;
    private bool _syncing;

    public StickerManager(TrayViewModel viewModel, StickerState state, Func<Window, SessionItemViewModel, bool> confirmResume)
    {
        _viewModel = viewModel;
        _state = state;
        _confirmResume = confirmResume;
    }

    public IReadOnlyCollection<StickerWindow> Windows => _windows.Values;

    public static bool AskWithMessageBox(Window owner, SessionItemViewModel session) =>
        MessageBox.Show(
            owner,
            $"Resume “{session.DisplayName}” with its full history in a new terminal?",
            "Copilot Session Tray",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _viewModel.Sessions.CollectionChanged += OnSessionsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        foreach (var session in _viewModel.Sessions)
        {
            Watch(session);
        }

        Sync();
    }

    public void Dispose()
    {
        if (_started)
        {
            _viewModel.Sessions.CollectionChanged -= OnSessionsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            foreach (var session in _watched.ToList())
            {
                Unwatch(session);
            }
        }

        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }

        _windows.Clear();
        _snoozed.Clear();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var session in _watched.ToList())
            {
                Unwatch(session);
            }

            foreach (var session in _viewModel.Sessions)
            {
                Watch(session);
            }
        }
        else
        {
            foreach (var session in e.OldItems?.OfType<SessionItemViewModel>() ?? [])
            {
                Unwatch(session);
            }

            foreach (var session in e.NewItems?.OfType<SessionItemViewModel>() ?? [])
            {
                Watch(session);
            }
        }

        Sync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrayViewModel.StickersVisible):
                Sync();
                break;
            case nameof(TrayViewModel.StickerOpacity):
                foreach (var window in _windows.Values)
                {
                    window.Opacity = _viewModel.StickerOpacity;
                }

                break;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionItemViewModel.HasSticker) or nameof(SessionItemViewModel.IsStickerHidden) or nameof(SessionItemViewModel.Status))
        {
            Sync();
        }
    }

    private void Watch(SessionItemViewModel session)
    {
        if (_watched.Add(session))
        {
            session.PropertyChanged += OnRowChanged;
        }
    }

    private void Unwatch(SessionItemViewModel session)
    {
        if (_watched.Remove(session))
        {
            session.PropertyChanged -= OnRowChanged;
        }
    }

    private void Sync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            var rows = _viewModel.Sessions.ToList();
            var pinnedRows = rows.Where(row => row.HasSticker).ToList();

            foreach (var (id, status) in _snoozed.ToList())
            {
                var row = pinnedRows.FirstOrDefault(candidate => candidate.Id == id);
                if (row is null || row.Status != status || !row.IsStickerHidden)
                {
                    _snoozed.Remove(id);
                    if (row is not null)
                    {
                        row.IsStickerHidden = false;
                    }
                }
            }

            foreach (var id in _windows.Keys.ToList())
            {
                if (pinnedRows.All(row => row.Id != id))
                {
                    _windows[id].Close();
                    _windows.Remove(id);
                }
            }

            var pinOrder = _state.Pinned.Select((item, index) => (item.SessionId, index)).ToDictionary(pair => pair.SessionId, pair => pair.index);
            var creating = pinnedRows
                .Where(row => !_windows.ContainsKey(row.Id))
                .OrderBy(row => pinOrder.TryGetValue(row.Id, out var index) ? index : int.MaxValue)
                .ToList();
            foreach (var row in creating.Where(row => _state.GetPlacement(row.Id).HasPosition))
            {
                Create(row);
            }

            foreach (var row in creating.Where(row => !_state.GetPlacement(row.Id).HasPosition))
            {
                Create(row);
            }

            foreach (var row in pinnedRows)
            {
                var show = _viewModel.StickersVisible && !_snoozed.ContainsKey(row.Id);
                _windows[row.Id].Visibility = show ? Visibility.Visible : Visibility.Hidden;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Create(SessionItemViewModel row)
    {
        var placement = _state.GetPlacement(row.Id);
        var workArea = ToRect(SystemParameters.WorkArea);
        var (width, height) = StickerLayout.ClampSize(placement.Width, placement.Height, workArea);

        StickerRect bounds;
        if (placement.HasPosition)
        {
            var remembered = new StickerRect(placement.Left!.Value, placement.Top!.Value, width, height);
            bounds = StickerLayout.IsReachable(VirtualScreen(), remembered)
                ? remembered
                : StickerLayout.FindSlot(workArea, width, height, OccupiedBounds());
        }
        else
        {
            bounds = StickerLayout.FindSlot(workArea, width, height, OccupiedBounds());
        }

        var window = new StickerWindow(row) { Opacity = _viewModel.StickerOpacity };
        window.Place(bounds);
        window.HideRequested += (_, _) => Snooze(row);
        window.ResumeRequested += (_, _) => Resume(window, row);
        window.PlacementCommitted += (_, _) => _state.SavePlacement(row.Id, new StickerPlacement(window.Left, window.Top, window.Width, window.Height));
        _windows[row.Id] = window;
    }

    private void Snooze(SessionItemViewModel row)
    {
        _snoozed[row.Id] = row.Status;
        row.IsStickerHidden = true;
        Sync();
    }

    private void Resume(StickerWindow window, SessionItemViewModel row)
    {
        if (_confirmResume(window, row))
        {
            _viewModel.ResumeSessionCommand.Execute(row);
        }
    }

    private List<StickerRect> OccupiedBounds() => _windows.Values.Select(window => window.PlacedBounds).ToList();

    private static StickerRect ToRect(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static StickerRect VirtualScreen() => new(
        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
}
