using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionLabelsTests : IDisposable
{
    private readonly FakeStore _store = new();
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"session-labels-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_GivesNothing_ForBlankText(string? blank)
    {
        Assert.Null(SessionLabels.Normalize(blank));
    }

    [Theory]
    [InlineData("review", "review")]
    [InlineData("  review  ", "review")]
    [InlineData("needs\r\n  a \t look", "needs a look")]
    public void Normalize_TrimsAndJoinsTheTextIntoOneLine(string typed, string expected)
    {
        Assert.Equal(expected, SessionLabels.Normalize(typed));
    }

    [Fact]
    public void Normalize_KeepsALabelOfExactlyTheMaximumLength_AndCutsALongerOneWithAnEllipsis()
    {
        var exact = new string('x', SessionLabels.MaxLength);
        var longer = new string('y', SessionLabels.MaxLength + 30);

        Assert.Equal(exact, SessionLabels.Normalize(exact));
        var cut = SessionLabels.Normalize(longer)!;
        Assert.Equal(SessionLabels.MaxLength, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void BeforeLoading_NothingIsLabelled()
    {
        Assert.Null(new SessionLabels(_store).Get("a"));
    }

    [Fact]
    public async Task LoadAsync_ReadsTheSavedLabels()
    {
        _store.Stored["a"] = new SessionLabel("bug", 3);
        var labels = new SessionLabels(_store);

        await labels.LoadAsync();

        Assert.Equal(new SessionLabel("bug", 3), labels.Get("a"));
        Assert.Null(labels.Get("b"));
        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task SetAsync_GivesALabel_AndSavesIt()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();

        var label = await labels.SetAsync("a", "bug");

        Assert.Equal(new SessionLabel("bug", 0), label);
        Assert.Equal(label, labels.Get("a"));
        Assert.Equal([new SessionLabel("bug", 0)], _store.Saves.Single().Values);
    }

    [Fact]
    public async Task SetAsync_KeepsTheTextTrimmedOnOneLine()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();

        var label = await labels.SetAsync("a", "  needs\r\n   a look ");

        Assert.Equal("needs a look", label!.Text);
        Assert.Equal("needs a look", _store.Stored["a"].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SetAsync_WithBlankText_RemovesTheLabel_AndSavesTheRemoval(string? blank)
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("a", "bug");
        await labels.SetAsync("b", "docs");
        _store.Saves.Clear();

        var result = await labels.SetAsync("a", blank);

        Assert.Null(result);
        Assert.Null(labels.Get("a"));
        Assert.Equal("docs", labels.Get("b")!.Text);
        Assert.Equal(["b"], _store.Saves.Single().Keys);
    }

    [Fact]
    public async Task SetAsync_WritesNothing_WhenNothingChanges()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("a", "bug");
        _store.Saves.Clear();

        await labels.SetAsync("a", "bug");
        await labels.SetAsync("a", "  bug ");
        await labels.SetAsync("b", "  ");
        await labels.SetAsync("  ", "bug");

        Assert.Empty(_store.Saves);
        Assert.Equal("bug", labels.Get("a")!.Text);
        Assert.Null(labels.Get("b"));
    }

    [Fact]
    public async Task SetAsync_ChangesTheTextOfAnExistingLabel()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("a", "bug");

        await labels.SetAsync("a", "docs");

        Assert.Equal("docs", labels.Get("a")!.Text);
        Assert.Equal("docs", _store.Stored["a"].Text);
    }

    [Fact]
    public async Task TheFirstLabelTakesTheFirstColor_AndNewTextsTakeTheNextUnusedOnes()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();

        var first = await labels.SetAsync("s1", "bug");
        var second = await labels.SetAsync("s2", "docs");
        var third = await labels.SetAsync("s3", "review");

        Assert.Equal([0, 1, 2], new[] { first!.ColorIndex, second!.ColorIndex, third!.ColorIndex });
    }

    [Fact]
    public async Task SessionsWithTheSameText_ShareAColor_WhateverTheCase()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        var first = await labels.SetAsync("s1", "bug");
        await labels.SetAsync("s2", "docs");

        var again = await labels.SetAsync("s3", "BUG");

        Assert.Equal(first!.ColorIndex, again!.ColorIndex);
        Assert.Equal("BUG", again.Text);
    }

    [Fact]
    public async Task ChangingOneOfTwoSessionsThatShareALabel_GivesItAColorOfItsOwn_AndLeavesTheOther()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("s1", "bug");
        await labels.SetAsync("s2", "bug");
        await labels.SetAsync("s3", "docs");

        var changed = await labels.SetAsync("s2", "review");

        Assert.Equal(2, changed!.ColorIndex);
        Assert.Equal(0, labels.Get("s1")!.ColorIndex);
        Assert.Equal(1, labels.Get("s3")!.ColorIndex);
    }

    [Fact]
    public async Task ChangingTheOnlyLabelToAnotherText_KeepsItsColor_ButNotWhileAnotherSessionSharesIt()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("s1", "bug");
        await labels.SetAsync("s2", "docs");

        var alone = await labels.SetAsync("s1", "review");
        await labels.SetAsync("s3", "review");
        await labels.SetAsync("s4", "later");
        await labels.SetAsync("s5", "later");
        var shared = await labels.SetAsync("s4", "other");

        Assert.Equal(0, alone!.ColorIndex);
        Assert.Equal(3, shared!.ColorIndex);
    }

    [Fact]
    public async Task ChangingOnlyTheCaseOfALabel_KeepsItsColor()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("s1", "bug");
        await labels.SetAsync("s2", "docs");
        await labels.SetAsync("s1", null);

        var changed = await labels.SetAsync("s2", "Docs");

        Assert.Equal(1, changed!.ColorIndex);
    }

    [Fact]
    public async Task TheColorOfARemovedLabel_IsFreeAgain()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        await labels.SetAsync("s1", "bug");
        await labels.SetAsync("s2", "docs");
        await labels.SetAsync("s1", null);

        var next = await labels.SetAsync("s3", "review");

        Assert.Equal(0, next!.ColorIndex);
    }

    [Fact]
    public async Task WhenEveryColorIsInUse_ANewTextTakesTheLeastUsedOne_CountingEachTextOnce()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        for (var i = 0; i < SessionLabelPalette.Count; i++)
        {
            await labels.SetAsync($"s{i}", $"text {i}");
        }

        await labels.SetAsync("more-1", "text 0");
        await labels.SetAsync("more-2", "text 0");
        var extra = await labels.SetAsync("more-3", "extra");
        var another = await labels.SetAsync("more-4", "another");

        Assert.Equal(0, extra!.ColorIndex);
        Assert.Equal(1, another!.ColorIndex);
    }

    [Fact]
    public async Task ASaveThatFailsBecauseTheFileIsBusy_KeepsTheLabel_AndTheNextChangeWritesEverything()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        _store.FailNextSave = new IOException("busy");

        await labels.SetAsync("a", "bug");

        Assert.Equal("bug", labels.Get("a")!.Text);
        Assert.Empty(_store.Saves);

        await labels.SetAsync("b", "docs");

        Assert.Equal(["a", "b"], _store.Saves.Single().Keys.Order());
    }

    [Fact]
    public async Task ASaveThatFailsWhileOthersWait_IsRetriedByTheNextWriterInLine()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldNextSave = release.Task;
        _store.FailOnSave = 2;

        var first = labels.SetAsync("s0", "zero");
        var second = labels.SetAsync("s1", "one");
        var third = labels.SetAsync("s2", "two");
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["s0", "s1", "s2"], _store.Stored.Keys.Order());
    }

    [Fact]
    public async Task ASaveThatFailsForAnyOtherReason_IsNotSwallowed()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        _store.FailNextSave = new InvalidOperationException("unexpected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => labels.SetAsync("a", "bug"));
    }

    [Fact]
    public async Task ChangesMadeWhileASaveIsInFlight_EndWithTheLatestStateSaved()
    {
        var labels = new SessionLabels(_store);
        await labels.LoadAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldNextSave = release.Task;

        var add = labels.SetAsync("a", "bug");
        var remove = labels.SetAsync("a", null);
        var other = labels.SetAsync("b", "docs");
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(add, remove, other);

        Assert.Equal(["b"], _store.Stored.Keys);
        Assert.Equal(["a"], _store.Saves[0].Keys);
        Assert.Equal(["b"], _store.Saves[^1].Keys);
    }

    [Fact]
    public async Task ALoadThatFailedAtStartUp_IsRetriedBeforeAChange_SoSavedLabelsAreNotOverwritten()
    {
        _store.Stored["old"] = new SessionLabel("kept", 4);
        _store.FailNextLoad = new IOException("busy");
        var labels = new SessionLabels(_store);

        await Assert.ThrowsAsync<IOException>(() => labels.LoadAsync());
        Assert.Null(labels.Get("old"));

        await labels.SetAsync("new", "added");

        Assert.Equal(["new", "old"], _store.Saves.Single().Keys.Order());
        Assert.Equal("kept", labels.Get("old")!.Text);
    }

    [Fact]
    public async Task Labels_SurviveARestart_ThroughTheRealStore()
    {
        var first = new SessionLabels(new AppStateStore(_stateFilePath));
        await first.LoadAsync();
        await first.SetAsync("s1", "bug");
        await first.SetAsync("s2", "docs");
        await first.SetAsync("s3", "bug");
        await first.SetAsync("s2", null);

        var second = new SessionLabels(new AppStateStore(_stateFilePath));
        await second.LoadAsync();

        Assert.Equal(new SessionLabel("bug", 0), second.Get("s1"));
        Assert.Equal(new SessionLabel("bug", 0), second.Get("s3"));
        Assert.Null(second.Get("s2"));
    }

    private sealed class FakeStore : IAppStateStore
    {
        public List<IReadOnlyDictionary<string, SessionLabel>> Saves { get; } = new();

        public Dictionary<string, SessionLabel> Stored { get; set; } = new();

        public Exception? FailNextSave { get; set; }

        public Exception? FailNextLoad { get; set; }

        public int? FailOnSave { get; set; }

        public Task? HoldNextSave { get; set; }

        private int _saveCalls;

        public Task<IReadOnlyDictionary<string, SessionLabel>> GetSessionLabelsAsync(CancellationToken cancellationToken = default)
        {
            if (FailNextLoad is { } failure)
            {
                FailNextLoad = null;
                throw failure;
            }

            return Task.FromResult<IReadOnlyDictionary<string, SessionLabel>>(new Dictionary<string, SessionLabel>(Stored));
        }

        public async Task SaveSessionLabelsAsync(IReadOnlyDictionary<string, SessionLabel> labels, CancellationToken cancellationToken = default)
        {
            var call = ++_saveCalls;
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

            if (FailOnSave == call)
            {
                throw new IOException("busy");
            }

            Saves.Add(new Dictionary<string, SessionLabel>(labels));
            Stored = new Dictionary<string, SessionLabel>(labels);
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

        public Task<IReadOnlyList<TerminalLaunch>> GetTerminalLaunchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveTerminalLaunchesAsync(IReadOnlyList<TerminalLaunch> launches, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
