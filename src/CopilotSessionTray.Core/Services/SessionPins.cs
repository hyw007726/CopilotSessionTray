using CopilotSessionTray.Core.Contracts;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// The sessions pinned to the top of the panel: an in-memory list that the rest of the app reads
/// synchronously, written back through <see cref="IAppStateStore"/> after every change.
/// </summary>
public sealed class SessionPins
{
    private readonly IAppStateStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<string> _ids = new();
    private bool _dirty;

    public SessionPins(IAppStateStore store)
    {
        _store = store;
    }

    /// <summary>The pinned session ids, oldest pin first.</summary>
    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return _ids.ToList();
            }
        }
    }

    public bool IsPinned(string sessionId)
    {
        lock (_gate)
        {
            return _ids.Contains(sessionId);
        }
    }

    /// <summary>
    /// Loads the saved pins. When none have ever been saved, the pins start as
    /// <paramref name="initialIds"/> and are saved straight away, so that happens only once.
    /// </summary>
    /// <param name="initialIds">The sessions to pin on a first run.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task LoadAsync(IEnumerable<string> initialIds, CancellationToken cancellationToken = default)
    {
        var saved = await _store.GetPinnedSessionIdsAsync(cancellationToken).ConfigureAwait(false);
        var firstRun = saved is null;
        lock (_gate)
        {
            _ids = firstRun
                ? initialIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList()
                : saved!.ToList();
            _dirty = firstRun;
        }

        if (firstRun)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pins or unpins a session and saves the result. A write that fails because the file is busy
    /// or not writable is retried together with the next change, so the pin is kept meanwhile.
    /// </summary>
    /// <param name="sessionId">The session to pin or unpin.</param>
    /// <param name="pinned">Whether it should be pinned.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            var index = _ids.IndexOf(sessionId);
            if (pinned == (index >= 0))
            {
                return;
            }

            if (pinned)
            {
                _ids.Add(sessionId);
            }
            else
            {
                _ids.RemoveAt(index);
            }

            _dirty = true;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                snapshot = _ids.ToList();
            }

            try
            {
                await _store.SavePinnedSessionIdsAsync(snapshot, cancellationToken).ConfigureAwait(false);
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
}
