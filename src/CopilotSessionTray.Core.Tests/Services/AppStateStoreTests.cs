using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="AppStateStore"/> against a throwaway temp file (via its testability
/// constructor overload) — never the real per-user <c>app-state.json</c> — so runs are
/// deterministic and never touch this developer's own real app state.
/// </summary>
public sealed class AppStateStoreTests : IDisposable
{
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"appstate-tests-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    private AppStateStore CreateStore() => new(_stateFilePath);

    [Fact]
    public async Task GetReadMarkerAsync_ReturnsNull_WhenNeverSaved()
    {
        IAppStateStore store = CreateStore();

        var marker = await store.GetReadMarkerAsync("session-1");

        Assert.Null(marker);
    }

    [Fact]
    public async Task SaveReadMarkerAsync_RoundTripsAllFields()
    {
        IAppStateStore store = CreateStore();
        var savedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await store.SaveReadMarkerAsync(new SessionReadMarker("session-1", 123, savedAt, IsDismissed: true));
        var marker = await store.GetReadMarkerAsync("session-1");

        Assert.NotNull(marker);
        Assert.Equal("session-1", marker!.SessionId);
        Assert.Equal(123, marker.LastAcknowledgedEventOffset);
        Assert.Equal(savedAt, marker.LastAcknowledgedUtc);
        Assert.True(marker.IsDismissed);
    }

    [Fact]
    public async Task SaveReadMarkerAsync_Overwrites_WhenCalledAgainForSameSession()
    {
        IAppStateStore store = CreateStore();

        await store.SaveReadMarkerAsync(new SessionReadMarker("session-1", 1, DateTimeOffset.UtcNow, IsDismissed: false));
        await store.SaveReadMarkerAsync(new SessionReadMarker("session-1", 2, DateTimeOffset.UtcNow, IsDismissed: true));
        var marker = await store.GetReadMarkerAsync("session-1");

        Assert.Equal(2, marker!.LastAcknowledgedEventOffset);
        Assert.True(marker.IsDismissed);
    }

    [Fact]
    public async Task ReadMarkers_ForDifferentSessions_DoNotInterfere()
    {
        IAppStateStore store = CreateStore();

        await store.SaveReadMarkerAsync(new SessionReadMarker("session-a", 1, DateTimeOffset.UtcNow, IsDismissed: true));
        await store.SaveReadMarkerAsync(new SessionReadMarker("session-b", 2, DateTimeOffset.UtcNow, IsDismissed: false));

        Assert.True((await store.GetReadMarkerAsync("session-a"))!.IsDismissed);
        Assert.False((await store.GetReadMarkerAsync("session-b"))!.IsDismissed);
    }

    [Fact]
    public async Task GetPreferencesAsync_ReturnsSensibleDefaults_WhenNeverSaved()
    {
        IAppStateStore store = CreateStore();

        var preferences = await store.GetPreferencesAsync();

        Assert.False(preferences.IsMuted);
        Assert.Null(preferences.QuietHoursStart);
        Assert.Empty(preferences.WatchedRepositories);
        Assert.Null(preferences.LastNewTaskWorkspaceDirectory);
    }

    [Fact]
    public async Task SavePreferencesAsync_RoundTrips_IncludingLastWorkspaceDirectory()
    {
        IAppStateStore store = CreateStore();
        var preferences = new NotificationPreferences(
            IsMuted: true,
            QuietHoursStart: new TimeOnly(22, 0),
            QuietHoursEnd: new TimeOnly(7, 0),
            WatchedRepositories: ["octocat/hello-world"],
            LastNewTaskWorkspaceDirectory: @"C:\Repos\example");

        await store.SavePreferencesAsync(preferences);
        var loaded = await store.GetPreferencesAsync();

        Assert.True(loaded.IsMuted);
        Assert.Equal(new TimeOnly(22, 0), loaded.QuietHoursStart);
        Assert.Equal(@"C:\Repos\example", loaded.LastNewTaskWorkspaceDirectory);
        Assert.Equal(["octocat/hello-world"], loaded.WatchedRepositories);
    }

    [Fact]
    public async Task GetCustomDisplayNameAsync_ReturnsNull_WhenNeverSet()
    {
        IAppStateStore store = CreateStore();

        Assert.Null(await store.GetCustomDisplayNameAsync("session-1"));
    }

    [Fact]
    public async Task SetCustomDisplayNameAsync_RoundTrips()
    {
        IAppStateStore store = CreateStore();

        await store.SetCustomDisplayNameAsync("session-1", "My renamed session");

        Assert.Equal("My renamed session", await store.GetCustomDisplayNameAsync("session-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SetCustomDisplayNameAsync_Clears_WhenNullOrEmpty(string? clearValue)
    {
        IAppStateStore store = CreateStore();
        await store.SetCustomDisplayNameAsync("session-1", "Some name");

        await store.SetCustomDisplayNameAsync("session-1", clearValue);

        Assert.Null(await store.GetCustomDisplayNameAsync("session-1"));
    }

    [Fact]
    public async Task State_PersistsAcrossSeparateStoreInstances_PointedAtSameFile()
    {
        await CreateStore().SaveReadMarkerAsync(new SessionReadMarker("session-1", 5, DateTimeOffset.UtcNow, IsDismissed: true));

        // A brand-new instance (simulating a fresh app launch) pointed at the same file should
        // see what a previous instance saved — this is the whole point of persisting to disk.
        var reopened = CreateStore();
        var marker = await reopened.GetReadMarkerAsync("session-1");

        Assert.NotNull(marker);
        Assert.True(marker!.IsDismissed);
    }

    [Fact]
    public async Task LoadUnlockedAsync_TreatsCorruptFile_AsEmpty_RatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        await File.WriteAllTextAsync(_stateFilePath, "{ this is not valid json ");

        IAppStateStore store = CreateStore();
        var preferences = await store.GetPreferencesAsync();
        var marker = await store.GetReadMarkerAsync("session-1");

        Assert.False(preferences.IsMuted); // defaults, not a thrown exception.
        Assert.Null(marker);
    }

    [Fact]
    public async Task MutateAsync_CreatesParentDirectory_WhenMissing()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"appstate-tests-nested-{Guid.NewGuid():N}", "app-state.json");
        try
        {
            IAppStateStore store = new AppStateStore(nestedPath);

            await store.SetCustomDisplayNameAsync("session-1", "Nested dir test");

            Assert.True(File.Exists(nestedPath));
        }
        finally
        {
            var dir = Path.GetDirectoryName(nestedPath)!;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
