using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class StickerStateTests : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private readonly ManualTimeProvider _time = new();
    private readonly FakeStore _store = new();
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"sticker-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    private StickerState Create() => new(_store, QuietPeriod, _time);

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
    public void BeforeLoading_StickersAreVisible_WithTheDefaultOpacityAndNoPins()
    {
        using var state = Create();

        Assert.True(state.Visible);
        Assert.Equal(StickerSettings.DefaultOpacity, state.Opacity);
        Assert.Empty(state.Pinned);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheStoredSettingsAndPins()
    {
        var pinnedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var placement = new StickerPlacement(10, 20, 300, 150);
        _store.Stored = new StickerSnapshot(new StickerSettings(false, 0.5), [new PinnedSticker("a", pinnedAt, placement), new PinnedSticker("b", pinnedAt.AddHours(1), StickerPlacement.None)]);
        using var state = Create();

        await state.LoadAsync();

        Assert.False(state.Visible);
        Assert.Equal(0.5, state.Opacity);
        Assert.Equal(["a", "b"], state.Pinned.Select(item => item.SessionId).ToArray());
        Assert.True(state.IsPinned("a"));
        Assert.False(state.IsPinned("c"));
        Assert.Equal(placement, state.GetPlacement("a"));
        Assert.Equal(StickerPlacement.None, state.GetPlacement("c"));
    }

    [Fact]
    public async Task Changes_AreCoalescedIntoOneWrite_AfterTheQuietPeriod()
    {
        using var state = Create();

        state.SetOpacity(0.5);
        state.SetOpacity(0.6);
        state.SetVisible(false);
        _time.Advance(QuietPeriod - TimeSpan.FromMilliseconds(1));
        await Settle();
        Assert.Empty(_store.Saves);
        _time.Advance(TimeSpan.FromMilliseconds(2));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Equal(new StickerSettings(false, 0.6), _store.Saves[0].Settings);
    }

    [Fact]
    public async Task ANewChange_RestartsTheQuietPeriod()
    {
        using var state = Create();

        state.SetOpacity(0.5);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        state.SetOpacity(0.6);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await Settle();
        Assert.Empty(_store.Saves);
        _time.Advance(TimeSpan.FromMilliseconds(101));

        await Eventually(() => _store.Saves.Count == 1);
        Assert.Equal(0.6, _store.Saves[0].Settings.Opacity);
    }

    [Fact]
    public async Task FlushAsync_WritesPendingChangesAtOnce_AndTheTimerDoesNotWriteThemAgain()
    {
        using var state = Create();
        state.SetPinned("a", true);

        await state.FlushAsync();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(_store.Saves);
        Assert.Equal("a", Assert.Single(_store.Saves[0].Pinned).SessionId);
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
    public void Dispose_WritesPendingChanges()
    {
        var state = Create();
        state.SetOpacity(0.4);

        state.Dispose();

        Assert.Equal(0.4, Assert.Single(_store.Saves).Settings.Opacity);
    }

    [Fact]
    public void Dispose_WritesPendingChanges_WhenTheCallersContextWillNeverRunAgain()
    {
        _store.YieldBeforeSaving = true;
        var state = Create();
        state.SetOpacity(0.4);
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
        Assert.Equal(0.4, Assert.Single(_store.Saves).Settings.Opacity);
    }

    [Fact]
    public void SetPinned_KeepsTheOrderTheyWerePinnedIn()
    {
        using var state = Create();

        state.SetPinned("a", true);
        _time.Advance(TimeSpan.FromMinutes(1));
        state.SetPinned("b", true);

        var pinned = state.Pinned;
        Assert.Equal(["a", "b"], pinned.Select(item => item.SessionId).ToArray());
        Assert.True(pinned[0].PinnedAtUtc < pinned[1].PinnedAtUtc);
    }

    [Fact]
    public async Task SetPinned_IgnoresRepeatedPins_AndUnpinsOfSessionsThatWereNeverPinned()
    {
        using var state = Create();

        state.SetPinned("a", true);
        state.SetPinned("a", true);
        state.SetPinned("never", false);
        await state.FlushAsync();

        Assert.Single(state.Pinned);
        Assert.Single(_store.Saves);
    }

    [Fact]
    public void Unpinning_ForgetsTheStickersPlacement()
    {
        using var state = Create();
        state.SetPinned("a", true);
        state.SavePlacement("a", new StickerPlacement(1, 2, 300, 150));

        state.SetPinned("a", false);
        state.SetPinned("a", true);

        Assert.Equal(StickerPlacement.None, state.GetPlacement("a"));
    }

    [Fact]
    public void SavePlacement_IsRemembered_ForPinnedSessionsOnly()
    {
        using var state = Create();
        var placement = new StickerPlacement(1, 2, 300, 150);

        state.SavePlacement("a", placement);
        Assert.Equal(StickerPlacement.None, state.GetPlacement("a"));

        state.SetPinned("a", true);
        state.SavePlacement("a", placement);
        Assert.Equal(placement, state.GetPlacement("a"));
    }

    [Fact]
    public void SavePlacement_DropsPartsThatAreNotFiniteOrNotPositive()
    {
        using var state = Create();
        state.SetPinned("a", true);

        state.SavePlacement("a", new StickerPlacement(double.NaN, 5, -3, 100));

        Assert.Equal(new StickerPlacement(null, 5, null, 100), state.GetPlacement("a"));
    }

    [Theory]
    [InlineData(0.05, StickerSettings.MinOpacity)]
    [InlineData(7, StickerSettings.MaxOpacity)]
    [InlineData(double.NaN, StickerSettings.DefaultOpacity)]
    [InlineData(0.55, 0.55)]
    public void SetOpacity_KeepsTheValueWithinTheAllowedRange(double requested, double expected)
    {
        using var state = Create();

        state.SetOpacity(requested);

        Assert.Equal(expected, state.Opacity);
    }

    [Fact]
    public async Task AFailedWrite_IsRetriedByTheNextFlush()
    {
        using var state = Create();
        _store.FailNextSave = new IOException("disk busy");
        state.SetPinned("a", true);

        _time.Advance(QuietPeriod + TimeSpan.FromMilliseconds(1));
        await Eventually(() => _store.FailNextSave is null);
        Assert.Empty(_store.Saves);

        await state.FlushAsync();
        Assert.Equal("a", Assert.Single(Assert.Single(_store.Saves).Pinned).SessionId);
    }

    [Fact]
    public async Task EverythingSurvivesARestart_ThroughTheRealStore()
    {
        var store = new AppStateStore(_stateFilePath);
        using (var first = new StickerState(store, QuietPeriod, _time))
        {
            first.SetPinned("a", true);
            first.SavePlacement("a", new StickerPlacement(12, 34, 280, 140));
            first.SetOpacity(0.55);
            first.SetVisible(false);
        }

        using var second = new StickerState(new AppStateStore(_stateFilePath), QuietPeriod, _time);
        await second.LoadAsync();

        Assert.False(second.Visible);
        Assert.Equal(0.55, second.Opacity);
        Assert.Equal(new StickerPlacement(12, 34, 280, 140), second.GetPlacement("a"));
    }

    private sealed class NeverPumpedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private sealed class FakeStore : IAppStateStore
    {
        public List<StickerSnapshot> Saves { get; } = new();

        public StickerSnapshot Stored { get; set; } = new(new StickerSettings(), []);

        public Exception? FailNextSave { get; set; }

        public bool YieldBeforeSaving { get; set; }

        public Task<StickerSnapshot> GetStickersAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored);

        public async Task SaveStickersAsync(StickerSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (YieldBeforeSaving)
            {
                await Task.Yield();
            }

            if (FailNextSave is { } failure)
            {
                FailNextSave = null;
                throw failure;
            }

            Saves.Add(snapshot);
            Stored = snapshot;
        }

        public Task<SessionReadMarker?> GetReadMarkerAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveReadMarkerAsync(SessionReadMarker marker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SavePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetCustomDisplayNameAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetCustomDisplayNameAsync(string sessionId, string? customDisplayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(timer => timer.DueAt is { } at && at <= _now).ToList();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Register(ManualTimer timer)
        {
            lock (_gate)
            {
                if (!_timers.Contains(timer))
                {
                    _timers.Add(timer);
                }
            }
        }

        private void Unregister(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    _owner.Unregister(this);
                }
                else
                {
                    DueAt = _owner.GetUtcNow() + dueTime;
                    _owner.Register(this);
                }

                return true;
            }

            public void Fire()
            {
                DueAt = null;
                _owner.Unregister(this);
                _callback(_state);
            }

            public void Dispose()
            {
                DueAt = null;
                _owner.Unregister(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
