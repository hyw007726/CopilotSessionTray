using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="IAppStateStore"/> — persists this
/// app's own state (read markers, preferences, custom display names) as a
/// single small JSON file under <c>%LOCALAPPDATA%\CopilotSessionTray\</c>,
/// entirely separate from anything under <c>.copilot</c>. A
/// <see cref="SemaphoreSlim"/> serializes read-modify-write operations
/// within this process — unlike <c>.copilot</c>'s files, this file has no
/// other writer to retry against, so no busy/retry handling is needed, only
/// protection against two calls in this process racing each other.
/// </summary>
public sealed class AppStateStore : IAppStateStore
{
    private static readonly string DefaultStateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopilotSessionTray", "app-state.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Creates a store against the real <c>app-state.json</c>. An explicit
    /// <paramref name="stateFilePath"/> override exists solely so xUnit tests can point this at a
    /// temp file instead of the real per-user profile location — every real call site uses the
    /// parameterless default.
    /// </summary>
    public AppStateStore(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? DefaultStateFilePath;
    }

    public async Task<SessionReadMarker?> GetReadMarkerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.ReadMarkers.TryGetValue(sessionId, out var marker)
            ? new SessionReadMarker(sessionId, marker.LastAcknowledgedEventOffset, marker.LastAcknowledgedUtc, marker.IsDismissed)
            : null;
    }

    public async Task SaveReadMarkerAsync(SessionReadMarker marker, CancellationToken cancellationToken = default)
    {
        await MutateAsync(state =>
        {
            state.ReadMarkers[marker.SessionId] = new StoredReadMarker(
                marker.LastAcknowledgedEventOffset, marker.LastAcknowledgedUtc, marker.IsDismissed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.Preferences ?? new NotificationPreferences(
            IsMuted: false, QuietHoursStart: null, QuietHoursEnd: null,
            WatchedRepositories: Array.Empty<string>(), LastNewTaskWorkspaceDirectory: null);
    }

    public async Task SavePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        await MutateAsync(state => state.Preferences = preferences, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetCustomDisplayNameAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.CustomDisplayNames.TryGetValue(sessionId, out var name) ? name : null;
    }

    public async Task SetCustomDisplayNameAsync(string sessionId, string? customDisplayName, CancellationToken cancellationToken = default)
    {
        await MutateAsync(state =>
        {
            if (string.IsNullOrEmpty(customDisplayName))
            {
                state.CustomDisplayNames.Remove(sessionId);
            }
            else
            {
                state.CustomDisplayNames[sessionId] = customDisplayName;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredState> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task MutateAsync(Action<StoredState> mutate, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            mutate(state);

            var directory = Path.GetDirectoryName(_stateFilePath)!;
            Directory.CreateDirectory(directory);
            await using var stream = File.Create(_stateFilePath);
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<StoredState> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new StoredState();
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await JsonSerializer.DeserializeAsync<StoredState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new StoredState();
        }
        catch (JsonException)
        {
            // Corrupt/partially-written state file — treat as empty rather than crash; the next
            // successful save will overwrite it with well-formed content.
            return new StoredState();
        }
    }

    private sealed class StoredState
    {
        public NotificationPreferences? Preferences { get; set; }

        public Dictionary<string, StoredReadMarker> ReadMarkers { get; set; } = new();

        public Dictionary<string, string> CustomDisplayNames { get; set; } = new();
    }

    private sealed record StoredReadMarker(long LastAcknowledgedEventOffset, DateTimeOffset LastAcknowledgedUtc, bool IsDismissed);
}
