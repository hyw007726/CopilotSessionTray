using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// In-memory copy of the persisted sticker state (settings, pins and remembered placements) that
/// the rest of the app reads and updates synchronously. Changes are written back through
/// <see cref="IAppStateStore"/> after a short quiet period; <see cref="Dispose"/> writes anything pending.
/// </summary>
public sealed class StickerState : IDisposable
{
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(3);

    private readonly IAppStateStore _store;
    private readonly TimeSpan _saveDelay;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private StickerSettings _settings = new();
    private List<PinnedSticker> _pinned = new();
    private CancellationTokenSource? _pendingSave;
    private bool _dirty;

    public StickerState(IAppStateStore store)
        : this(store, TimeSpan.FromMilliseconds(400), TimeProvider.System)
    {
    }

    internal StickerState(IAppStateStore store, TimeSpan saveDelay, TimeProvider time)
    {
        _store = store;
        _saveDelay = saveDelay;
        _time = time;
    }

    public bool Visible
    {
        get
        {
            lock (_gate)
            {
                return _settings.Visible;
            }
        }
    }

    public double Opacity
    {
        get
        {
            lock (_gate)
            {
                return _settings.Opacity;
            }
        }
    }

    public IReadOnlyList<PinnedSticker> Pinned
    {
        get
        {
            lock (_gate)
            {
                return _pinned.ToList();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetStickersAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _settings = snapshot.Settings;
            _pinned = snapshot.Pinned.ToList();
        }
    }

    public bool IsPinned(string sessionId)
    {
        lock (_gate)
        {
            return _pinned.Any(item => item.SessionId == sessionId);
        }
    }

    public StickerPlacement GetPlacement(string sessionId)
    {
        lock (_gate)
        {
            return _pinned.FirstOrDefault(item => item.SessionId == sessionId)?.Placement ?? StickerPlacement.None;
        }
    }

    public void SetVisible(bool visible)
    {
        Update(() =>
        {
            if (_settings.Visible == visible)
            {
                return false;
            }

            _settings = _settings with { Visible = visible };
            return true;
        });
    }

    public void SetOpacity(double opacity)
    {
        var clamped = StickerSettings.ClampOpacity(opacity);
        Update(() =>
        {
            if (_settings.Opacity.Equals(clamped))
            {
                return false;
            }

            _settings = _settings with { Opacity = clamped };
            return true;
        });
    }

    public void SetPinned(string sessionId, bool pinned)
    {
        Update(() =>
        {
            var index = _pinned.FindIndex(item => item.SessionId == sessionId);
            if (pinned && index < 0)
            {
                _pinned.Add(new PinnedSticker(sessionId, _time.GetUtcNow(), StickerPlacement.None));
                return true;
            }

            if (!pinned && index >= 0)
            {
                _pinned.RemoveAt(index);
                return true;
            }

            return false;
        });
    }

    public void SavePlacement(string sessionId, StickerPlacement placement)
    {
        var clean = new StickerPlacement(Finite(placement.Left), Finite(placement.Top), PositiveFinite(placement.Width), PositiveFinite(placement.Height));
        Update(() =>
        {
            var index = _pinned.FindIndex(item => item.SessionId == sessionId);
            if (index < 0 || _pinned[index].Placement == clean)
            {
                return false;
            }

            _pinned[index] = _pinned[index] with { Placement = clean };
            return true;
        });
    }

    public async Task FlushAsync()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pendingSave;
            _pendingSave = null;
        }

        pending?.Cancel();
        pending?.Dispose();
        await SaveNowAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        var flush = Task.Run(FlushAsync);
        try
        {
            if (!flush.Wait(DisposeFlushTimeout))
            {
                return;
            }
        }
        catch (AggregateException)
        {
        }

        _saveGate.Dispose();
    }

    private void Update(Func<bool> change)
    {
        CancellationTokenSource token;
        lock (_gate)
        {
            if (!change())
            {
                return;
            }

            _dirty = true;
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            token = _pendingSave = new CancellationTokenSource();
        }

        _ = SaveLaterAsync(token.Token);
    }

    private async Task SaveLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_saveDelay, _time, cancellationToken).ConfigureAwait(false);
            await SaveNowAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task SaveNowAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StickerSnapshot snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                snapshot = new StickerSnapshot(_settings, _pinned.ToList());
            }

            try
            {
                await _store.SaveStickersAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    _dirty = true;
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static double? PositiveFinite(double? value) => value is { } number && double.IsFinite(number) && number > 0 ? number : null;
}
