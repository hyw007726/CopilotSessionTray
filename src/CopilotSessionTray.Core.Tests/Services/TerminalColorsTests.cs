using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class TerminalColorsTests : IDisposable
{
    private readonly FakeStore _store = new();
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"terminal-colors-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    [Fact]
    public async Task TheFirstTerminal_GetsTheFirstColor()
    {
        var colors = new TerminalColors(_store);

        Assert.Equal(TerminalPalette.At(0), await colors.PickAsync(null));
    }

    [Fact]
    public async Task TerminalsOpenedOneAfterAnother_AllLookDifferent_AndTheLeastRecentColorComesRoundAgainWhenThePaletteRunsOut()
    {
        var colors = new TerminalColors(_store);
        var picked = new List<TerminalColor>();

        for (var i = 0; i < TerminalPalette.Count; i++)
        {
            picked.Add(await colors.PickAsync(null));
        }

        Assert.Equal(TerminalPalette.Colors, picked);
        Assert.Equal(TerminalPalette.At(0), await colors.PickAsync(null));
        Assert.Equal(TerminalPalette.At(1), await colors.PickAsync(null));
    }

    [Fact]
    public async Task AfterARestart_TheNextTerminalGetsAColorThatIsNotInUse_InsteadOfTheFirstColorAgain()
    {
        var before = new TerminalColors(new AppStateStore(_stateFilePath));
        var first = new[] { await before.PickAsync(null), await before.PickAsync("a"), await before.PickAsync(null) };

        var after = new TerminalColors(new AppStateStore(_stateFilePath));
        var second = new[] { await after.PickAsync(null), await after.PickAsync(null), await after.PickAsync(null) };

        Assert.Equal([TerminalPalette.At(0), TerminalPalette.At(1), TerminalPalette.At(2)], first);
        Assert.Equal([TerminalPalette.At(3), TerminalPalette.At(4), TerminalPalette.At(5)], second);
    }

    [Fact]
    public async Task AResumedSession_GetsItsColorBack_EvenAfterOtherTerminalsAndARestart()
    {
        var before = new TerminalColors(new AppStateStore(_stateFilePath));
        var session1 = await before.PickAsync("session-1");
        await before.PickAsync(null);
        var session2 = await before.PickAsync("session-2");

        var after = new TerminalColors(new AppStateStore(_stateFilePath));

        Assert.Equal(session1, await after.PickAsync("session-1"));
        Assert.Equal(session2, await after.PickAsync("session-2"));
        Assert.NotEqual(session1, session2);
    }

    [Fact]
    public async Task AResumedSession_GetsTheColorItHadBefore_WhenNoOtherTerminalTookItSince()
    {
        _store.Stored = [new TerminalLaunch(2, "other"), new TerminalLaunch(1, "session-1")];
        var colors = new TerminalColors(_store);

        Assert.Equal(TerminalPalette.At(1), await colors.PickAsync("session-1"));
    }

    [Fact]
    public async Task AResumedSession_GetsAnotherColor_WhenAnotherTerminalTookItsColorSince()
    {
        _store.Stored = [new TerminalLaunch(1, "other"), new TerminalLaunch(1, "session-1")];
        var colors = new TerminalColors(_store);

        Assert.Equal(TerminalPalette.At(0), await colors.PickAsync("session-1"));
    }

    [Fact]
    public async Task TwoTerminalsOfTheSameSession_ShareItsColor_WhileANewSessionGetsAnother()
    {
        var colors = new TerminalColors(_store);

        var first = await colors.PickAsync("session-1");
        var second = await colors.PickAsync("session-1");
        var other = await colors.PickAsync(null);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankSessionId_IsANewSession(string blank)
    {
        var colors = new TerminalColors(_store);

        var first = await colors.PickAsync(blank);
        var second = await colors.PickAsync(blank);

        Assert.NotEqual(first, second);
        Assert.All(_store.Saves.Last(), launch => Assert.Null(launch.SessionId));
    }

    [Fact]
    public async Task EveryChoice_IsSaved_NewestFirst()
    {
        var colors = new TerminalColors(_store);

        await colors.PickAsync("a");
        await colors.PickAsync(null);

        Assert.Equal(2, _store.Saves.Count);
        Assert.Equal([new TerminalLaunch(0, "a")], _store.Saves[0]);
        Assert.Equal([new TerminalLaunch(1, null), new TerminalLaunch(0, "a")], _store.Saves[1]);
    }

    [Fact]
    public async Task OnlyTheLastHundredTerminals_AreRemembered()
    {
        var colors = new TerminalColors(_store);

        for (var i = 0; i < TerminalColors.HistoryLimit + 25; i++)
        {
            await colors.PickAsync($"session-{i}");
        }

        var saved = _store.Saves.Last();
        Assert.Equal(100, TerminalColors.HistoryLimit);
        Assert.Equal(TerminalColors.HistoryLimit, saved.Count);
        Assert.Equal($"session-{TerminalColors.HistoryLimit + 24}", saved[0].SessionId);
        Assert.Equal("session-25", saved[^1].SessionId);
    }

    [Fact]
    public async Task ASavedHistoryLongerThanTheLimit_IsCutDownWithTheNextTerminal()
    {
        _store.Stored = Enumerable.Range(0, 300).Select(i => new TerminalLaunch(i % TerminalPalette.Count, $"s{i}")).ToList();
        var colors = new TerminalColors(_store);

        await colors.PickAsync(null);

        var saved = _store.Saves.Single();
        Assert.Equal(TerminalColors.HistoryLimit, saved.Count);
        Assert.Equal("s0", saved[1].SessionId);
        Assert.Equal("s98", saved[^1].SessionId);
    }

    [Fact]
    public async Task ASlotOutsideThePalette_InTheSavedHistory_IsReadAsItsWrappedSlot()
    {
        _store.Stored = [new TerminalLaunch(-1, "a"), new TerminalLaunch(25, null)];
        var colors = new TerminalColors(_store);

        Assert.Equal(TerminalPalette.At(-1), await colors.PickAsync("a"));
        Assert.Equal(new TerminalLaunch(TerminalPalette.Count - 1, "a"), _store.Saves.Single()[0]);
        Assert.Equal(TerminalPalette.At(0), await colors.PickAsync(null));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task ASaveThatFails_NeverStopsATerminalFromOpening_AndTheNextOneSavesEverything(Type failure)
    {
        _store.FailNextSave = (Exception)Activator.CreateInstance(failure, "busy")!;
        var colors = new TerminalColors(_store);

        var first = await colors.PickAsync("a");
        var second = await colors.PickAsync(null);

        Assert.Equal(TerminalPalette.At(0), first);
        Assert.Equal(TerminalPalette.At(1), second);
        Assert.Equal([new TerminalLaunch(1, null), new TerminalLaunch(0, "a")], _store.Saves.Single());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task SavedLaunchesThatCannotBeRead_AreNeverOverwritten_AndAreUsedOnceTheyCanBeRead(Type failure)
    {
        _store.Stored = [new TerminalLaunch(3, "kept")];
        _store.FailNextLoad = (Exception)Activator.CreateInstance(failure, "busy")!;
        var colors = new TerminalColors(_store);

        var first = await colors.PickAsync(null);

        Assert.Equal(TerminalPalette.At(0), first);
        Assert.Empty(_store.Saves);

        var second = await colors.PickAsync(null);

        Assert.Equal(TerminalPalette.At(1), second);
        Assert.Equal([new TerminalLaunch(1, null), new TerminalLaunch(0, null), new TerminalLaunch(3, "kept")], _store.Saves.Single());
    }

    [Fact]
    public async Task AFailureThatIsNotAboutTheFile_IsNotHidden()
    {
        _store.FailNextLoad = new InvalidOperationException("broken");
        var colors = new TerminalColors(_store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => colors.PickAsync(null));
    }

    [Fact]
    public async Task TerminalsOpenedAtTheSameTime_GetDifferentColors_AndTheSavedHistoryIsReadOnce()
    {
        _store.Stored = [new TerminalLaunch(TerminalPalette.Count - 1, "old")];
        _store.Delay = TimeSpan.FromMilliseconds(5);
        var colors = new TerminalColors(_store);

        var picked = await Task.WhenAll(Enumerable.Range(0, TerminalPalette.Count).Select(_ => colors.PickAsync(null)));

        Assert.Equal(TerminalPalette.Count, picked.Distinct().Count());
        var saved = _store.Saves.Last();
        Assert.Equal(TerminalPalette.Count + 1, saved.Count);
        Assert.Single(saved, launch => launch.SessionId == "old");
        Assert.Equal(1, _store.Loads);
    }

    private sealed class FakeStore : IAppStateStore
    {
        public List<IReadOnlyList<TerminalLaunch>> Saves { get; } = new();

        public List<TerminalLaunch> Stored { get; set; } = new();

        public Exception? FailNextSave { get; set; }

        public Exception? FailNextLoad { get; set; }

        public TimeSpan Delay { get; set; }

        public int Loads { get; private set; }

        public async Task<IReadOnlyList<TerminalLaunch>> GetTerminalLaunchesAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (FailNextLoad is { } failure)
            {
                FailNextLoad = null;
                throw failure;
            }

            return Stored.ToList();
        }

        public async Task SaveTerminalLaunchesAsync(IReadOnlyList<TerminalLaunch> launches, CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (FailNextSave is { } failure)
            {
                FailNextSave = null;
                throw failure;
            }

            Saves.Add(launches.ToList());
            Stored = launches.ToList();
        }

        public Task<SessionReadMarker?> GetReadMarkerAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveReadMarkerAsync(SessionReadMarker marker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SavePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetCustomDisplayNameAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetCustomDisplayNameAsync(string sessionId, string? customDisplayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StickerSnapshot> GetStickersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveStickersAsync(StickerSnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetPinnedSessionIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SavePinnedSessionIdsAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, SessionLabel>> GetSessionLabelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveSessionLabelsAsync(IReadOnlyDictionary<string, SessionLabel> labels, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
