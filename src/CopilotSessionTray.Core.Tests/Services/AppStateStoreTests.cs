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
        Assert.False(preferences.ShowPinnedOnly);
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
    public async Task SavePreferencesAsync_RoundTrips_ShowPinnedOnly_BothWays()
    {
        IAppStateStore store = CreateStore();
        var preferences = new NotificationPreferences(false, null, null, Array.Empty<string>());

        await store.SavePreferencesAsync(preferences with { ShowPinnedOnly = true });
        Assert.True((await store.GetPreferencesAsync()).ShowPinnedOnly);

        await store.SavePreferencesAsync(preferences with { ShowPinnedOnly = false });
        Assert.False((await store.GetPreferencesAsync()).ShowPinnedOnly);
    }

    [Fact]
    public async Task GetPreferencesAsync_ReadsShowPinnedOnlyAsFalse_FromAFileSavedBeforeItExisted()
    {
        await File.WriteAllTextAsync(_stateFilePath, """
            {
              "preferences": {
                "isMuted": true,
                "quietHoursStart": null,
                "quietHoursEnd": null,
                "watchedRepositories": [],
                "lastNewTaskWorkspaceDirectory": "C:\\Work"
              },
              "readMarkers": {},
              "customDisplayNames": {}
            }
            """);
        IAppStateStore store = CreateStore();

        var preferences = await store.GetPreferencesAsync();

        Assert.False(preferences.ShowPinnedOnly);
        Assert.True(preferences.IsMuted);
        Assert.Equal(@"C:\Work", preferences.LastNewTaskWorkspaceDirectory);
    }

    [Fact]
    public async Task SavePreferencesAsync_AddsShowPinnedOnlyToThatFile_AndKeepsTheRest()
    {
        await File.WriteAllTextAsync(_stateFilePath, """
            {
              "preferences": {
                "isMuted": true,
                "quietHoursStart": null,
                "quietHoursEnd": null,
                "watchedRepositories": [],
                "lastNewTaskWorkspaceDirectory": "C:\\Work"
              },
              "pinnedSessions": ["session-1"]
            }
            """);
        IAppStateStore store = CreateStore();

        var preferences = await store.GetPreferencesAsync();
        await store.SavePreferencesAsync(preferences with { ShowPinnedOnly = true });

        var loaded = await store.GetPreferencesAsync();
        Assert.True(loaded.ShowPinnedOnly);
        Assert.True(loaded.IsMuted);
        Assert.Equal(@"C:\Work", loaded.LastNewTaskWorkspaceDirectory);
        Assert.Equal(["session-1"], await store.GetPinnedSessionIdsAsync());
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
    public async Task GetStickersAsync_ReturnsDefaults_WhenNeverSaved()
    {
        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal(new StickerSettings(), snapshot.Settings);
        Assert.Empty(snapshot.Pinned);
    }

    [Fact]
    public async Task SaveStickersAsync_RoundTripsSettingsPinsAndPlacements()
    {
        var first = new PinnedSticker("session-1", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), new StickerPlacement(10, 20, 300, 150));
        var second = new PinnedSticker("session-2", new DateTimeOffset(2026, 1, 3, 3, 4, 5, TimeSpan.Zero), StickerPlacement.None);

        await CreateStore().SaveStickersAsync(new StickerSnapshot(new StickerSettings(false, 0.45), [first, second]));
        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal(new StickerSettings(false, 0.45), snapshot.Settings);
        Assert.Equal([first, second], snapshot.Pinned);
    }

    [Fact]
    public async Task SaveStickersAsync_LeavesTheRestOfTheStateAlone()
    {
        IAppStateStore store = CreateStore();
        await store.SetCustomDisplayNameAsync("session-1", "My name");
        await store.SaveReadMarkerAsync(new SessionReadMarker("session-1", 7, DateTimeOffset.UtcNow, IsDismissed: true));

        await store.SaveStickersAsync(new StickerSnapshot(new StickerSettings(true, 0.8), []));

        Assert.Equal("My name", await store.GetCustomDisplayNameAsync("session-1"));
        Assert.True((await store.GetReadMarkerAsync("session-1"))!.IsDismissed);
    }

    [Fact]
    public async Task GetStickersAsync_ToleratesAFileWrittenBeforeStickersExisted()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{\"readMarkers\":{},\"customDisplayNames\":{\"session-1\":\"My name\"}}");

        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal(new StickerSettings(), snapshot.Settings);
        Assert.Empty(snapshot.Pinned);
    }

    [Fact]
    public async Task GetStickersAsync_CleansUpAHandEditedFile()
    {
        await File.WriteAllTextAsync(
            _stateFilePath,
            "{\"stickers\":{\"visible\":true,\"opacity\":7,\"pinned\":[" +
            "{\"sessionId\":\"b\",\"pinnedAtUtc\":\"2026-02-01T00:00:00Z\",\"left\":10,\"width\":-5}," +
            "{\"sessionId\":\"a\",\"pinnedAtUtc\":\"2026-01-01T00:00:00Z\"}," +
            "{\"sessionId\":\"a\",\"pinnedAtUtc\":\"2026-03-01T00:00:00Z\"}," +
            "{\"sessionId\":\"  \",\"pinnedAtUtc\":\"2026-01-01T00:00:00Z\"}]}}");

        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal(StickerSettings.MaxOpacity, snapshot.Settings.Opacity);
        Assert.Equal(["a", "b"], snapshot.Pinned.Select(item => item.SessionId).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), snapshot.Pinned[0].PinnedAtUtc);
        Assert.Equal(new StickerPlacement(10, null, null, null), snapshot.Pinned[1].Placement);
    }

    [Fact]
    public async Task SaveStickersAsync_RoundTripsTheStatusAStickerWasHiddenAt()
    {
        var shown = new PinnedSticker("session-1", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), StickerPlacement.None);
        var hidden = new PinnedSticker("session-2", new DateTimeOffset(2026, 1, 3, 3, 4, 5, TimeSpan.Zero), new StickerPlacement(10, 20, 300, 150), SessionStatus.Closed);

        await CreateStore().SaveStickersAsync(new StickerSnapshot(new StickerSettings(), [shown, hidden]));
        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal([shown, hidden], snapshot.Pinned);
        Assert.Null(snapshot.Pinned[0].HiddenAtStatus);
        Assert.Equal(SessionStatus.Closed, snapshot.Pinned[1].HiddenAtStatus);
    }

    [Theory]
    [InlineData("\"hiddenAtStatus\":\"Working\"", SessionStatus.Working)]
    [InlineData("\"hiddenAtStatus\":\"WaitingForInput\"", SessionStatus.WaitingForInput)]
    [InlineData("\"hiddenAtStatus\":\"Nonsense\"", null)]
    [InlineData("\"hiddenAtStatus\":\"working\"", null)]
    [InlineData("\"hiddenAtStatus\":\"1\"", null)]
    [InlineData("\"hiddenAtStatus\":null", null)]
    [InlineData("\"left\":1", null)]
    public async Task GetStickersAsync_ReadsTheHiddenStatus_AndIgnoresAnythingThatIsNotAStatusName(string field, SessionStatus? expected)
    {
        await File.WriteAllTextAsync(
            _stateFilePath,
            "{\"stickers\":{\"visible\":true,\"opacity\":0.9,\"pinned\":[{\"sessionId\":\"a\",\"pinnedAtUtc\":\"2026-01-01T00:00:00Z\"," + field + "}]}}");

        var snapshot = await CreateStore().GetStickersAsync();

        Assert.Equal(expected, Assert.Single(snapshot.Pinned).HiddenAtStatus);
    }

    [Fact]
    public async Task GetPinnedSessionIdsAsync_ReturnsNull_WhenNeverSaved()
    {
        Assert.Null(await CreateStore().GetPinnedSessionIdsAsync());
    }

    [Fact]
    public async Task GetPinnedSessionIdsAsync_ToleratesAFileWrittenBeforePinsExisted()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{\"readMarkers\":{},\"stickers\":{\"visible\":true,\"opacity\":0.9,\"pinned\":[]}}");

        Assert.Null(await CreateStore().GetPinnedSessionIdsAsync());
    }

    [Fact]
    public async Task SavePinnedSessionIdsAsync_RoundTripsInOrder()
    {
        await CreateStore().SavePinnedSessionIdsAsync(["session-2", "session-1", "session-3"]);

        Assert.Equal(["session-2", "session-1", "session-3"], await CreateStore().GetPinnedSessionIdsAsync());
    }

    [Fact]
    public async Task SavePinnedSessionIdsAsync_AnEmptyList_IsSavedAsEmpty_NotAsNeverSaved()
    {
        await CreateStore().SavePinnedSessionIdsAsync(["session-1"]);
        await CreateStore().SavePinnedSessionIdsAsync([]);

        var saved = await CreateStore().GetPinnedSessionIdsAsync();

        Assert.NotNull(saved);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task PinsAndStickers_DoNotOverwriteEachOther()
    {
        IAppStateStore store = CreateStore();
        var sticker = new PinnedSticker("session-9", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), StickerPlacement.None);

        await store.SavePinnedSessionIdsAsync(["session-1"]);
        await store.SaveStickersAsync(new StickerSnapshot(new StickerSettings(false, 0.6), [sticker]));
        await store.SetCustomDisplayNameAsync("session-1", "My name");

        Assert.Equal(["session-1"], await store.GetPinnedSessionIdsAsync());
        Assert.Equal([sticker], (await store.GetStickersAsync()).Pinned);
        Assert.Equal("My name", await store.GetCustomDisplayNameAsync("session-1"));

        await store.SavePinnedSessionIdsAsync(["session-2"]);

        Assert.Equal([sticker], (await store.GetStickersAsync()).Pinned);
        Assert.Equal("My name", await store.GetCustomDisplayNameAsync("session-1"));
    }

    [Fact]
    public async Task GetPinnedSessionIdsAsync_CleansUpAHandEditedFile()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{\"pinnedSessions\":[\"b\",null,\"  \",\"a\",\"b\",\"\"]}");

        Assert.Equal(["b", "a"], await CreateStore().GetPinnedSessionIdsAsync());
    }

    [Fact]
    public async Task GetSessionLabelsAsync_ReturnsNothing_WhenNeverSaved()
    {
        Assert.Empty(await CreateStore().GetSessionLabelsAsync());
    }

    [Fact]
    public async Task SaveSessionLabelsAsync_RoundTripsTextAndColor_AndAnEmptyMapClearsThem()
    {
        IAppStateStore store = CreateStore();
        var labels = new Dictionary<string, SessionLabel>
        {
            ["session-1"] = new("bug", 0),
            ["session-2"] = new("needs a look", 7),
        };

        await store.SaveSessionLabelsAsync(labels);
        var loaded = await CreateStore().GetSessionLabelsAsync();

        Assert.Equal(labels, loaded);

        await store.SaveSessionLabelsAsync(new Dictionary<string, SessionLabel>());

        Assert.Empty(await CreateStore().GetSessionLabelsAsync());
    }

    [Fact]
    public async Task SessionLabels_ReadTheDocumentedFileFormat_AndToleratesAFileWrittenBeforeTheyExisted()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{\"sessionLabels\":{\"a\":{\"text\":\"bug\",\"colorIndex\":2}}}");
        var labels = await CreateStore().GetSessionLabelsAsync();
        Assert.Equal(new SessionLabel("bug", 2), Assert.Single(labels).Value);

        await File.WriteAllTextAsync(_stateFilePath, "{\"readMarkers\":{},\"customDisplayNames\":{\"a\":\"My name\"},\"pinnedSessions\":[\"a\"]}");
        Assert.Empty(await CreateStore().GetSessionLabelsAsync());
    }

    [Fact]
    public async Task SessionLabels_AndTheRestOfTheState_DoNotOverwriteEachOther()
    {
        IAppStateStore store = CreateStore();
        var sticker = new PinnedSticker("session-9", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), StickerPlacement.None);

        await store.SetCustomDisplayNameAsync("session-1", "My name");
        await store.SavePinnedSessionIdsAsync(["session-1"]);
        await store.SaveStickersAsync(new StickerSnapshot(new StickerSettings(false, 0.6), [sticker]));
        await store.SaveSessionLabelsAsync(new Dictionary<string, SessionLabel> { ["session-1"] = new("bug", 1) });
        await store.SavePinnedSessionIdsAsync(["session-2"]);
        await store.SetCustomDisplayNameAsync("session-1", "Another name");

        Assert.Equal(new SessionLabel("bug", 1), Assert.Single(await store.GetSessionLabelsAsync()).Value);
        Assert.Equal("Another name", await store.GetCustomDisplayNameAsync("session-1"));
        Assert.Equal(["session-2"], await store.GetPinnedSessionIdsAsync());
        Assert.Equal([sticker], (await store.GetStickersAsync()).Pinned);
    }

    [Fact]
    public async Task GetSessionLabelsAsync_CleansUpAHandEditedFile()
    {
        await File.WriteAllTextAsync(
            _stateFilePath,
            "{\"sessionLabels\":{" +
            "\"a\":{\"text\":\"  spaced   out \",\"colorIndex\":-1}," +
            "\"b\":{\"text\":\"   \",\"colorIndex\":1}," +
            "\"c\":null," +
            "\"d\":{\"colorIndex\":3}," +
            "\"  \":{\"text\":\"no session\",\"colorIndex\":1}," +
            "\"e\":{\"text\":\"" + new string('x', 90) + "\",\"colorIndex\":25}}}");

        var labels = await CreateStore().GetSessionLabelsAsync();

        Assert.Equal(["a", "e"], labels.Keys.Order());
        Assert.Equal(new SessionLabel("spaced out", SessionLabelPalette.Count - 1), labels["a"]);
        Assert.Equal(SessionLabels.MaxLength, labels["e"].Text.Length);
        Assert.Equal(5, labels["e"].ColorIndex);
    }

    [Fact]
    public async Task GetTerminalLaunchesAsync_ReturnsNothing_WhenNeverSaved()
    {
        Assert.Empty(await CreateStore().GetTerminalLaunchesAsync());
    }

    [Fact]
    public async Task SaveTerminalLaunchesAsync_RoundTripsInOrder_AndAnEmptyListClearsThem()
    {
        IAppStateStore store = CreateStore();
        TerminalLaunch[] launches = [new(4, "session-1"), new(0, null), new(8, "session-2")];

        await store.SaveTerminalLaunchesAsync(launches);

        Assert.Equal(launches, await CreateStore().GetTerminalLaunchesAsync());

        await store.SaveTerminalLaunchesAsync([]);

        Assert.Empty(await CreateStore().GetTerminalLaunchesAsync());
    }

    [Fact]
    public async Task TerminalLaunches_ReadTheDocumentedFileFormat_AndToleratesAFileWrittenBeforeTheyExisted()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{\"terminalLaunches\":[{\"slot\":2,\"sessionId\":\"a\"},{\"slot\":5,\"sessionId\":null}]}");
        Assert.Equal([new TerminalLaunch(2, "a"), new TerminalLaunch(5, null)], await CreateStore().GetTerminalLaunchesAsync());

        await File.WriteAllTextAsync(_stateFilePath, "{\"readMarkers\":{},\"customDisplayNames\":{\"a\":\"My name\"},\"pinnedSessions\":[\"a\"]}");
        Assert.Empty(await CreateStore().GetTerminalLaunchesAsync());
    }

    [Fact]
    public async Task TerminalLaunches_AndTheRestOfTheState_DoNotOverwriteEachOther()
    {
        IAppStateStore store = CreateStore();

        await store.SetCustomDisplayNameAsync("session-1", "My name");
        await store.SaveSessionLabelsAsync(new Dictionary<string, SessionLabel> { ["session-1"] = new("bug", 1) });
        await store.SaveTerminalLaunchesAsync([new TerminalLaunch(3, "session-1")]);
        await store.SavePinnedSessionIdsAsync(["session-2"]);
        await store.SetCustomDisplayNameAsync("session-1", "Another name");

        Assert.Equal([new TerminalLaunch(3, "session-1")], await store.GetTerminalLaunchesAsync());
        Assert.Equal("Another name", await store.GetCustomDisplayNameAsync("session-1"));
        Assert.Equal(["session-2"], await store.GetPinnedSessionIdsAsync());
        Assert.Equal(new SessionLabel("bug", 1), Assert.Single(await store.GetSessionLabelsAsync()).Value);
    }

    [Fact]
    public async Task GetTerminalLaunchesAsync_CleansUpAHandEditedFile()
    {
        await File.WriteAllTextAsync(
            _stateFilePath,
            "{\"terminalLaunches\":[" +
            "{\"slot\":-1,\"sessionId\":\"a\"}," +
            "null," +
            "{\"slot\":25,\"sessionId\":\"   \"}," +
            "{\"sessionId\":\"b\"}]}");

        var launches = await CreateStore().GetTerminalLaunchesAsync();

        Assert.Equal(
            [new TerminalLaunch(TerminalPalette.Count - 1, "a"), new TerminalLaunch(7, null), new TerminalLaunch(0, "b")],
            launches);
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
