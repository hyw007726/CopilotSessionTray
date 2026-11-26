using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// In-memory copy of the unfinished input of the "Start a new task" panel, kept so it is still
/// there when the panel is closed and opened again, or the app is restarted. A draft exists only
/// while the prompt has text. Changes are written back through <see cref="IAppStateStore"/> after a
/// short quiet period; <see cref="Dispose"/> writes anything pending.
/// </summary>
public sealed class NewTaskDraftState : IDisposable
{
    private const int MaxSaveAttempts = 5;
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(3);

    private readonly IAppStateStore _store;
    private readonly TimeSpan _saveDelay;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private NewTaskDraft? _draft;
    private CancellationTokenSource? _pendingSave;
    private bool _loaded;
    private bool _touched;
    private bool _dirty;

    public NewTaskDraftState(IAppStateStore store)
        : this(store, TimeSpan.FromMilliseconds(400), TimeProvider.System)
    {
    }

    internal NewTaskDraftState(IAppStateStore store, TimeSpan saveDelay, TimeProvider time)
    {
        _store = store;
        _saveDelay = saveDelay;
        _time = time;
    }

    /// <summary>The unfinished input, or null when there is none.</summary>
    public NewTaskDraft? Current
    {
        get
        {
            lock (_gate)
            {
                return _draft;
            }
        }
    }

    /// <summary>Reads the saved draft once. Anything saved or cleared before the read finishes wins over it.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
        }

        var stored = await _store.GetNewTaskDraftAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _loaded = true;
            if (!_touched)
            {
                _draft = stored;
            }
        }
    }

    /// <summary>Keeps the panel's input as the draft; a prompt that is empty or only whitespace discards the draft.</summary>
    public void Save(string? prompt, string? workspaceDirectory, bool enableAllPermissions) =>
        Update(string.IsNullOrWhiteSpace(prompt) ? null : new NewTaskDraft(prompt, workspaceDirectory ?? string.Empty, enableAllPermissions));

    /// <summary>Discards the draft.</summary>
    public void Clear() => Update(null);

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

    private void Update(NewTaskDraft? draft)
    {
        CancellationToken token;
        lock (_gate)
        {
            _touched = true;
            if (_loaded && Equals(_draft, draft))
            {
                return;
            }

            _draft = draft;
            _dirty = true;
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = new CancellationTokenSource();
            token = _pendingSave.Token;
        }

        _ = SaveLaterAsync(token);
    }

    private async Task SaveLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
            {
                await Task.Delay(_saveDelay, _time, cancellationToken).ConfigureAwait(false);
                if (await SaveNowAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task<bool> SaveNowAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NewTaskDraft? snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return true;
                }

                _dirty = false;
                snapshot = _draft;
            }

            try
            {
                await _store.SaveNewTaskDraftAsync(snapshot).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    _dirty = true;
                }

                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
