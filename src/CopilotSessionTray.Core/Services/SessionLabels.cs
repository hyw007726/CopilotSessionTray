using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// The labels the user put on sessions: an in-memory map that the rest of the app reads
/// synchronously, written back through <see cref="IAppStateStore"/> after every change.
/// Labels with the same text (ignoring case) share a color, and a new text gets the palette slot
/// the fewest other texts use, so different labels look different for as long as the palette lasts.
/// </summary>
public sealed class SessionLabels
{
    /// <summary>The most characters a label can have.</summary>
    public const int MaxLength = 40;

    private readonly IAppStateStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Dictionary<string, SessionLabel> _labels = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _dirty;

    public SessionLabels(IAppStateStore store)
    {
        _store = store;
    }

    /// <summary>The label of a session, or <see langword="null"/> when it has none.</summary>
    public SessionLabel? Get(string sessionId)
    {
        lock (_gate)
        {
            return _labels.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// Turns what was typed into a label's text: one trimmed line of at most <see cref="MaxLength"/>
    /// characters, or <see langword="null"/> when nothing is left.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : SessionText.ToSingleLine(text, MaxLength);

    /// <summary>Loads the saved labels.</summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _store.GetSessionLabelsAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _labels = new Dictionary<string, SessionLabel>(saved, StringComparer.Ordinal);
            _loaded = true;
            _dirty = false;
        }
    }

    /// <summary>
    /// Gives a session a label, changes it, or removes it when <paramref name="text"/> is empty,
    /// and saves the result. A write that fails because the file is busy or not writable is retried
    /// together with the next change, so the label is kept meanwhile. If the saved labels could
    /// not be read at start-up they are read first, so a change never overwrites them.
    /// </summary>
    /// <param name="sessionId">The session to label.</param>
    /// <param name="text">The label as typed, or empty to remove it.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The label the session has now, or <see langword="null"/> when it has none.</returns>
    public async Task<SessionLabel?> SetAsync(string sessionId, string? text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (!IsLoaded)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        SessionLabel? result;
        lock (_gate)
        {
            var current = _labels.GetValueOrDefault(sessionId);
            var normalized = Normalize(text);
            if (normalized is null)
            {
                if (current is null)
                {
                    return null;
                }

                _labels.Remove(sessionId);
                result = null;
            }
            else
            {
                if (current is not null && current.Text == normalized)
                {
                    return current;
                }

                result = new SessionLabel(normalized, ColorFor(sessionId, normalized, current));
                _labels[sessionId] = result;
            }

            _dirty = true;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    private int ColorFor(string sessionId, string text, SessionLabel? current)
    {
        var others = _labels.Where(pair => pair.Key != sessionId).Select(pair => pair.Value).ToList();
        if (others.FirstOrDefault(label => SameText(label.Text, text)) is { } shared)
        {
            return SessionLabelPalette.Wrap(shared.ColorIndex);
        }

        if (current is not null && SameText(current.Text, text))
        {
            return SessionLabelPalette.Wrap(current.ColorIndex);
        }

        var uses = new int[SessionLabelPalette.Count];
        foreach (var distinct in others.GroupBy(label => label.Text, StringComparer.OrdinalIgnoreCase))
        {
            uses[SessionLabelPalette.Wrap(distinct.First().ColorIndex)]++;
        }

        return Array.IndexOf(uses, uses.Min());
    }

    private static bool SameText(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, SessionLabel> snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                snapshot = new Dictionary<string, SessionLabel>(_labels, StringComparer.Ordinal);
            }

            try
            {
                await _store.SaveSessionLabelsAsync(snapshot, cancellationToken).ConfigureAwait(false);
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
