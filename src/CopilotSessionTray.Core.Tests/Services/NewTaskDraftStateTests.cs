using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class NewTaskDraftStateTests : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private readonly ManualTimeProvider _time = new();
    private readonly FakeStore _store = new();
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"new-task-draft-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    private NewTaskDraftState Create() => new(_store, QuietPeriod, _time);

    private static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(100));

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    [Fact]
    public void BeforeLoading_ThereIsNoDraft()
    {
        using var state = Create();

        Assert.Null(state.Current);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheStoredDraft()
    {
        var stored = new NewTaskDraft("Fix the build", @"C:\Work", true);
        _store.Stored = stored;
        using var state = Create();

        await state.LoadAsync();

        Assert.Equal(stored, state.Current);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheStoreOnlyOnce()
    {
        _store.Stored = new NewTaskDraft("first", @"C:\Work", false);
        using var state = Create();

        await state.LoadAsync();
        _store.Stored = new NewTaskDraft("second", @"C:\Other", true);
        await state.LoadAsync();

        Assert.Equal(1, _store.Loads);
        Assert.Equal("first", state.Current!.Prompt);
    }

    [Fact]
    public async Task AFailedLoad_CanBeRetried()
    {
        var stored = new NewTaskDraft("Fix the build", @"C:\Work", false);
        _store.Stored = stored;
        _store.FailNextLoad = new IOException("disk busy");
        using var state = Create();

        await Assert.ThrowsAsync<IOException>(() => state.LoadAsync());
        Assert.Null(state.Current);

        await state.LoadAsync();

        Assert.Equal(stored, state.Current);
    }

    [Fact]
    public void Save_KeepsThePromptExactlyAsTyped_WithTheWorkspaceAndThePermissions()
    {
        using var state = Create();

        state.Save("  Fix it\r\n\r\n  then test  ", @"C:\Work", true);

        Assert.Equal(new NewTaskDraft("  Fix it\r\n\r\n  then test  ", @"C:\Work", true), state.Current);
    }

    [Fact]
    public void Save_WithoutAWorkspace_KeepsAnEmptyOne()
    {
        using var state = Create();

        state.Save("Fix it", null, false);

        Assert.Equal(new NewTaskDraft("Fix it", string.Empty, false), state.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public void Save_WithABlankPrompt_DiscardsTheDraft(string? prompt)
    {
        using var state = Create();
        state.Save("Fix it", @"C:\Work", true);

        state.Save(prompt, @"C:\Work", true);

        Assert.Null(state.Current);
    }

    [Fact]
    public void Clear_DiscardsTheDraft()
    {
        using var state = Create();
        state.Save("Fix it", @"C:\Work", true);

        state.Clear();

        Assert.Null(state.Current);
    }

    [Fact]
    public async Task ASaveBeforeTheLoadFinishes_WinsOverTheStoredDraft()
    {
        _store.Stored = new NewTaskDraft("old", @"C:\Old", false);
        var release = new TaskCompletionSource();
        _store.HoldLoad = release.Task;
        using var state = Create();

        var load = state.LoadAsync();
        state.Save("typed", @"C:\New", true);
        release.SetResult();
        await load;
        await state.FlushAsync();

        var typed = new NewTaskDraft("typed", @"C:\New", true);
        Assert.Equal(typed, state.Current);
        Assert.Equal(typed, Assert.Single(_store.Saves));
    }

    [Fact]
    public async Task AClearBeforeAnythingWasLoaded_StillRemovesTheStoredDraft()
    {
        _store.Stored = new NewTaskDraft("old", @"C:\Old", false);
        using var state = Create();

        state.Clear();
        await state.LoadAsync();
        await state.FlushAsync();

        Assert.Null(state.Current);
        Assert.Null(Assert.Single(_store.Saves));
        Assert.Null(_store.Stored);
    }

    [Fact]
    public async Task Changes_AreCoalescedIntoOneWrite_AfterTheQuietPeriod()
    {
        using var state = Create();

        state.Save("F", @"C:\Work", false);
        state.Save("Fi", @"C:\Work", false);
        state.Save("Fix", @"C:\Work", true);
        _time.Advance(QuietPeriod - TimeSpan.FromMilliseconds(1));
        await Settle();
        Assert.Empty(_store.Saves);
        _time.Advance(TimeSpan.FromMilliseconds(2));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Equal(new NewTaskDraft("Fix", @"C:\Work", true), _store.Saves[0]);
    }

    [Fact]
    public async Task ANewChange_RestartsTheQuietPeriod()
    {
        using var state = Create();

        state.Save("F", @"C:\Work", false);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        state.Save("Fi", @"C:\Work", false);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await Settle();
        Assert.Empty(_store.Saves);
        _time.Advance(TimeSpan.FromMilliseconds(101));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Equal("Fi", _store.Saves[0]!.Prompt);
    }

    [Fact]
    public async Task SavingThenClearing_WithinTheQuietPeriod_WritesOnlyTheRemoval()
    {
        _store.Stored = new NewTaskDraft("old", @"C:\Old", false);
        using var state = Create();
        await state.LoadAsync();

        state.Save("typing", @"C:\Work", false);
        state.Clear();
        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Null(_store.Saves[0]);
        Assert.Null(_store.Stored);
    }

    [Fact]
    public async Task FlushAsync_WritesPendingChangesAtOnce_AndTheTimerDoesNotWriteThemAgain()
    {
        using var state = Create();
        state.Save("Fix it", @"C:\Work", false);

        await state.FlushAsync();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("Fix it", Assert.Single(_store.Saves)!.Prompt);
    }

    [Fact]
    public async Task FlushAsync_WritesNothing_WhenNothingChanged()
    {
        using var state = Create();
        await state.LoadAsync();

        await state.FlushAsync();

        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task SavingTheSameInputAgain_WritesNothing()
    {
        using var state = Create();
        await state.LoadAsync();
        state.Save("Fix it", @"C:\Work", true);
        await state.FlushAsync();
        _store.Saves.Clear();

        state.Save("Fix it", @"C:\Work", true);
        await state.FlushAsync();

        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task ClearingWhenThereIsNoDraft_WritesNothing()
    {
        using var state = Create();
        await state.LoadAsync();

        state.Clear();
        state.Save("   ", @"C:\Work", true);
        await state.FlushAsync();

        Assert.Empty(_store.Saves);
    }

    [Fact]
    public void Dispose_WritesPendingChanges()
    {
        var state = Create();
        state.Save("Fix it", @"C:\Work", false);

        state.Dispose();

        Assert.Equal("Fix it", Assert.Single(_store.Saves)!.Prompt);
    }

    [Fact]
    public void Dispose_WritesPendingChanges_WhenTheCallersContextWillNeverRunAgain()
    {
        _store.YieldBeforeSaving = true;
        var state = Create();
        state.Save("Fix it", @"C:\Work", false);
        using var finished = new ManualResetEventSlim();
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedSynchronizationContext());
            state.Dispose();
            finished.Set();
        })
        {
            IsBackground = true,
        };

        caller.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("Fix it", Assert.Single(_store.Saves)!.Prompt);
    }

    [Fact]
    public async Task AFailedWrite_IsRetriedByTheNextFlush()
    {
        using var state = Create();
        _store.FailNextSave = new IOException("disk busy");
        state.Save("Fix it", @"C:\Work", false);

        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
        await Eventually(() => _store.FailNextSave is null);
        Assert.Empty(_store.Saves);

        await state.FlushAsync();
        Assert.Equal("Fix it", Assert.Single(_store.Saves)!.Prompt);
    }

    [Fact]
    public async Task AFailedWrite_IsRetriedAfterTheNextQuietPeriod()
    {
        using var state = Create();
        _store.FailSaves = 1;
        state.Save("Fix it", @"C:\Work", false);

        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
        await Eventually(() => _store.SaveCalls == 1);
        await Settle();
        Assert.Empty(_store.Saves);
        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Equal("Fix it", _store.Saves[0]!.Prompt);
    }

    [Fact]
    public async Task ARepeatedlyFailingWrite_IsRetriedAFewTimes_ThenLeftForTheNextFlush()
    {
        using var state = Create();
        _store.FailSaves = int.MaxValue;
        state.Save("Fix it", @"C:\Work", false);

        for (var round = 0; round < 8; round++)
        {
            _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
            await Settle();
        }

        var attempts = _store.SaveCalls;
        Assert.InRange(attempts, 2, 8);
        for (var round = 0; round < 3; round++)
        {
            _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
            await Settle();
        }

        Assert.Equal(attempts, _store.SaveCalls);
        _store.FailSaves = 0;
        await state.FlushAsync();
        Assert.Equal("Fix it", Assert.Single(_store.Saves)!.Prompt);
    }

    [Fact]
    public async Task ConcurrentChanges_EndWithTheStoreMatchingTheCurrentDraft()
    {
        using var state = Create();
        await state.LoadAsync();

        var workers = Enumerable.Range(0, 6).Select(worker => Task.Run(() =>
        {
            for (var step = 0; step < 100; step++)
            {
                if (step % 7 == 0)
                {
                    state.Clear();
                }
                else
                {
                    state.Save($"worker {worker}, step {step}", @"C:\Work", step % 2 == 0);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
        await state.FlushAsync();

        Assert.Equal(state.Current, _store.Stored);
    }

    [Fact]
    public async Task TheDraft_SurvivesARestart_ThroughTheRealStore()
    {
        using (var first = new NewTaskDraftState(new AppStateStore(_stateFilePath), QuietPeriod, _time))
        {
            first.Save("Fix the build\r\nthen test", @"C:\Work\Repo", true);
        }

        using var second = new NewTaskDraftState(new AppStateStore(_stateFilePath), QuietPeriod, _time);
        await second.LoadAsync();

        Assert.Equal(new NewTaskDraft("Fix the build\r\nthen test", @"C:\Work\Repo", true), second.Current);
    }

    [Fact]
    public async Task AClearedDraft_StaysGone_AfterARestart()
    {
        using (var first = new NewTaskDraftState(new AppStateStore(_stateFilePath), QuietPeriod, _time))
        {
            first.Save("Fix the build", @"C:\Work\Repo", false);
            await first.FlushAsync();
            first.Clear();
        }

        using var second = new NewTaskDraftState(new AppStateStore(_stateFilePath), QuietPeriod, _time);
        await second.LoadAsync();

        Assert.Null(second.Current);
    }

    private sealed class NeverPumpedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private sealed class FakeStore : IAppStateStore
    {
        public List<NewTaskDraft?> Saves { get; } = new();

        public NewTaskDraft? Stored { get; set; }

        public Exception? FailNextSave { get; set; }

        public int FailSaves { get; set; }

        public int SaveCalls { get; private set; }

        public Exception? FailNextLoad { get; set; }

        public bool YieldBeforeSaving { get; set; }

        public Task? HoldLoad { get; set; }

        public int Loads { get; private set; }

        public async Task<NewTaskDraft?> GetNewTaskDraftAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            if (HoldLoad is { } hold)
            {
                await hold;
            }

            if (FailNextLoad is { } failure)
            {
                FailNextLoad = null;
                throw failure;
            }

            return Stored;
        }

        public async Task SaveNewTaskDraftAsync(NewTaskDraft? draft, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (YieldBeforeSaving)
            {
                await Task.Yield();
            }

            if (FailSaves > 0)
            {
                FailSaves--;
                throw new IOException("disk busy");
            }

            if (FailNextSave is { } failure)
            {
                FailNextSave = null;
                throw failure;
            }

            Saves.Add(draft);
            Stored = draft;
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

        public Task<IReadOnlyList<TerminalLaunch>> GetTerminalLaunchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveTerminalLaunchesAsync(IReadOnlyList<TerminalLaunch> launches, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
