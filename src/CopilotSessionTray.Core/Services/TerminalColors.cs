using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Decides the color of every terminal this app opens, so terminals that are open together look
/// different. The terminals opened so far are remembered through <see cref="IAppStateStore"/>, so
/// the choice carries on across restarts of the app. A new terminal gets the color that was used
/// least recently; a resumed session gets the color it had last time, unless another terminal has
/// taken it since.
/// </summary>
public sealed class TerminalColors
{
    /// <summary>How many opened terminals are remembered.</summary>
    public const int HistoryLimit = 100;

    private readonly IAppStateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TerminalLaunch> _launches = new();
    private bool _loaded;

    public TerminalColors(IAppStateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Chooses the color for a terminal that is about to open and remembers the choice. A saved
    /// history that cannot be read or written never stops the terminal from opening: the choice is
    /// still made from what is known, and the history is saved again with the next terminal.
    /// </summary>
    /// <param name="sessionId">The session being resumed, or <see langword="null"/> for a new session.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task<TerminalColor> PickAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                _loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
            }

            var id = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
            var slot = ChooseSlot(id);
            _launches.Insert(0, new TerminalLaunch(slot, id));
            if (_launches.Count > HistoryLimit)
            {
                _launches.RemoveRange(HistoryLimit, _launches.Count - HistoryLimit);
            }

            if (_loaded)
            {
                await TrySaveAsync(cancellationToken).ConfigureAwait(false);
            }

            return TerminalPalette.At(slot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private int ChooseSlot(string? sessionId)
    {
        if (sessionId is not null)
        {
            var last = _launches.FindIndex(launch => launch.SessionId == sessionId);
            if (last >= 0)
            {
                var slot = TerminalPalette.Wrap(_launches[last].Slot);
                if (!_launches.Take(last).Any(launch => TerminalPalette.Wrap(launch.Slot) == slot))
                {
                    return slot;
                }
            }
        }

        var lastUse = new int[TerminalPalette.Count];
        Array.Fill(lastUse, int.MaxValue);
        for (var i = _launches.Count - 1; i >= 0; i--)
        {
            lastUse[TerminalPalette.Wrap(_launches[i].Slot)] = i;
        }

        var chosen = 0;
        for (var candidate = 1; candidate < lastUse.Length; candidate++)
        {
            if (lastUse[candidate] > lastUse[chosen])
            {
                chosen = candidate;
            }
        }

        return chosen;
    }

    private async Task<bool> TryLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            _launches.AddRange(await _store.GetTerminalLaunchesAsync(cancellationToken).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveTerminalLaunchesAsync(_launches.ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
