using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionPinsTests : IDisposable
{
    private readonly FakeStore _store = new();
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"session-pins-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    [Fact]
    public void BeforeLoading_NothingIsPinned()
    {
        var pins = new SessionPins(_store);

        Assert.Empty(pins.Ids);
        Assert.False(pins.IsPinned("a"));
    }

    [Fact]
    public async Task LoadAsync_UsesTheSavedPins_AndIgnoresTheInitialOnes()
    {
        _store.Stored = ["a", "b"];
        var pins = new SessionPins(_store);

        await pins.LoadAsync(["x"]);

        Assert.Equal(["a", "b"], pins.Ids);
        Assert.True(pins.IsPinned("a"));
        Assert.False(pins.IsPinned("x"));
        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task LoadAsync_WithNothingSaved_StartsFromTheInitialPins_AndSavesThem()
    {
        var pins = new SessionPins(_store);

        await pins.LoadAsync(["s2", "s1", "s2", "", "  "]);

        Assert.Equal(["s2", "s1"], pins.Ids);
        Assert.Equal([["s2", "s1"]], _store.Saves);
    }

    [Fact]
    public async Task LoadAsync_WithNothingSavedAndNothingToStartFrom_SavesAnEmptyList()
    {
        var pins = new SessionPins(_store);

        await pins.LoadAsync([]);

        Assert.Empty(pins.Ids);
        Assert.NotNull(_store.Stored);
        Assert.Empty(_store.Stored);
    }

    [Fact]
    public async Task LoadAsync_DoesNotStartFromTheInitialPinsAgain_OnceTheyHaveBeenSaved()
    {
        await new SessionPins(_store).LoadAsync(["s1"]);
        var later = new SessionPins(_store);

        await later.LoadAsync(["s1", "s2"]);

        Assert.Equal(["s1"], later.Ids);
    }

    [Fact]
    public async Task LoadAsync_DoesNotStartFromTheInitialPinsAgain_EvenWhenEverythingWasUnpinned()
    {
        var first = new SessionPins(_store);
        await first.LoadAsync(["s1"]);
        await first.SetPinnedAsync("s1", false);
        var later = new SessionPins(_store);

        await later.LoadAsync(["s1"]);

        Assert.Empty(later.Ids);
    }

    [Fact]
    public async Task SetPinnedAsync_PinsOldestFirst_AndUnpins_SavingEachChange()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        _store.Saves.Clear();

        await pins.SetPinnedAsync("b", true);
        await pins.SetPinnedAsync("a", true);
        await pins.SetPinnedAsync("b", false);

        Assert.Equal(["a"], pins.Ids);
        Assert.False(pins.IsPinned("b"));
        Assert.Equal([["b"], ["b", "a"], ["a"]], _store.Saves);
    }

    [Fact]
    public async Task SetPinnedAsync_WritesNothing_WhenNothingChanges()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        await pins.SetPinnedAsync("a", true);
        _store.Saves.Clear();

        await pins.SetPinnedAsync("a", true);
        await pins.SetPinnedAsync("b", false);

        Assert.Empty(_store.Saves);
        Assert.Equal(["a"], pins.Ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetPinnedAsync_IgnoresABlankId(string blank)
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        _store.Saves.Clear();

        await pins.SetPinnedAsync(blank, true);

        Assert.Empty(pins.Ids);
        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task Ids_IsACopy_ThatLaterChangesDoNotTouch()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync(["a"]);
        var before = pins.Ids;

        await pins.SetPinnedAsync("b", true);

        Assert.Equal(["a"], before);
        Assert.Equal(["a", "b"], pins.Ids);
    }

    [Fact]
    public async Task ChangesMadeWhileASaveIsInFlight_EndWithTheLatestStateSaved()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        _store.Saves.Clear();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldNextSave = release.Task;

        var pin = pins.SetPinnedAsync("a", true);
        var unpin = pins.SetPinnedAsync("a", false);
        var other = pins.SetPinnedAsync("b", true);
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(pin, unpin, other);

        Assert.Equal(["b"], _store.Stored);
        Assert.Equal(["a"], _store.Saves[0]);
        Assert.Equal(["b"], _store.Saves[^1]);
    }

    [Fact]
    public async Task ASaveThatFailsBecauseTheFileIsBusy_KeepsThePin_AndTheNextChangeWritesEverything()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        _store.Saves.Clear();
        _store.FailNextSave = new IOException("busy");

        await pins.SetPinnedAsync("a", true);

        Assert.True(pins.IsPinned("a"));
        Assert.Empty(_store.Saves);

        await pins.SetPinnedAsync("b", true);

        Assert.Equal([["a", "b"]], _store.Saves);
    }

    [Fact]
    public async Task ASaveThatFailsForAnyOtherReason_IsNotSwallowed()
    {
        var pins = new SessionPins(_store);
        await pins.LoadAsync([]);
        _store.FailNextSave = new InvalidOperationException("unexpected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pins.SetPinnedAsync("a", true));
    }

    [Fact]
    public async Task ExistingStickers_BecomePins_OnceOnly_ThroughTheRealStore()
    {
        var store = new AppStateStore(_stateFilePath);
        var oldest = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveStickersAsync(new StickerSnapshot(
            new StickerSettings(),
            [new PinnedSticker("s1", oldest, StickerPlacement.None), new PinnedSticker("s2", oldest.AddDays(1), StickerPlacement.None)]));

        var first = new SessionPins(store);
        await first.LoadAsync(["s1", "s2"]);
        await first.SetPinnedAsync("s1", false);
        var second = new SessionPins(new AppStateStore(_stateFilePath));
        await second.LoadAsync(["s1", "s2", "s3"]);

        Assert.Equal(["s2"], second.Ids);
        Assert.Equal(["s1", "s2"], (await store.GetStickersAsync()).Pinned.Select(item => item.SessionId).ToArray());
    }

    private sealed class FakeStore : IAppStateStore
    {
        public List<IReadOnlyList<string>> Saves { get; } = new();

        public IReadOnlyList<string>? Stored { get; set; }

        public Exception? FailNextSave { get; set; }

        public Task? HoldNextSave { get; set; }

        public Task<IReadOnlyList<string>?> GetPinnedSessionIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored);

        public async Task SavePinnedSessionIdsAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
        {
            var hold = HoldNextSave;
            HoldNextSave = null;
            if (hold is not null)
            {
                await hold;
            }

            if (FailNextSave is { } failure)
            {
                FailNextSave = null;
                throw failure;
            }

            Saves.Add(sessionIds.ToList());
            Stored = sessionIds.ToList();
        }

        public Task<SessionReadMarker?> GetReadMarkerAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveReadMarkerAsync(SessionReadMarker marker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SavePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetCustomDisplayNameAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetCustomDisplayNameAsync(string sessionId, string? customDisplayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StickerSnapshot> GetStickersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveStickersAsync(StickerSnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
