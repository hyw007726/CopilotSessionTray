using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class NeedsInputAlertsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly NeedsInputAlerts _alerts = new(Start);

    private static PendingPrompt Waiting(DateTimeOffset since, PendingPromptKind kind = PendingPromptKind.Permission) => new(kind, since);

    private static SessionChangeEvent Change(string session, SessionChangeKind kind, PendingPrompt prompt = default, DateTimeOffset? at = null) =>
        new(session, kind, SessionStatus.Working, at ?? Start.AddMinutes(1), null, Prompt: prompt);

    [Theory]
    [InlineData(SessionChangeKind.Started)]
    [InlineData(SessionChangeKind.WorkingStateChanged)]
    [InlineData(SessionChangeKind.Finished)]
    [InlineData(SessionChangeKind.NeedsInputChanged)]
    public void ANewWait_AlertsWhicheverChangeCarriesIt(SessionChangeKind kind)
    {
        var action = _alerts.Observe(Change("s1", kind, Waiting(Start.AddSeconds(30))));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Theory]
    [InlineData(PendingPromptKind.Permission)]
    [InlineData(PendingPromptKind.Question)]
    public void BothKindsOfWait_Alert(PendingPromptKind kind)
    {
        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30), kind)));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void ASessionThatIsNotWaiting_SaysNothing()
    {
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Started)));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Finished)));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Closed)));
    }

    [Fact]
    public void ASessionThatStartsWaiting_AfterBeingSeen_Alerts()
    {
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Started)));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddMinutes(2))));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void AWait_ThatBeganBeforeMonitoringDid_IsNotAlertedAbout()
    {
        var action = _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddMinutes(-5))));

        Assert.Equal(NeedsInputAction.None, action);
    }

    [Fact]
    public void AWait_ThatBeganExactlyWhenMonitoringDid_IsAlertedAbout()
    {
        var action = _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start)));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void AWait_ThatBeganJustBeforeMonitoringDid_IsNotAlertedAbout()
    {
        var action = _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddTicks(-1))));

        Assert.Equal(NeedsInputAction.None, action);
    }

    [Fact]
    public void AnOldWait_IsStillRemembered_SoItsAnswerTakesItsAlertDown()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddMinutes(-5))));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged));

        Assert.Equal(NeedsInputAction.Dismiss, action);
    }

    [Fact]
    public void ANewWait_AfterAnOldOneWasAnswered_Alerts()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddMinutes(-5))));
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddMinutes(3))));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void AnOldWait_ThatMovesToANewerRequest_DoesNotAlert()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddMinutes(-5))));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddMinutes(3))));

        Assert.Equal(NeedsInputAction.None, action);
    }

    [Fact]
    public void ARepeatOfTheSameWait_DoesNotAlertAgain()
    {
        Assert.Equal(NeedsInputAction.Alert, _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30)))));

        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(90)))));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Finished, Waiting(Start.AddSeconds(90)))));
    }

    [Fact]
    public void AWait_ThatChangesKind_DoesNotAlertAgain()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30), PendingPromptKind.Permission)));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(40), PendingPromptKind.Question)));

        Assert.Equal(NeedsInputAction.None, action);
    }

    [Fact]
    public void AnsweringTheWait_TakesTheAlertDown_Once()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30))));

        Assert.Equal(NeedsInputAction.Dismiss, _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged)));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged)));
    }

    [Fact]
    public void ANewWait_AfterAnAnswer_AlertsAgain()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30))));
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddMinutes(5))));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void ClosingAWaitingSession_TakesItsAlertDown()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30))));

        Assert.Equal(NeedsInputAction.Dismiss, _alerts.Observe(Change("s1", SessionChangeKind.Closed)));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Closed)));
    }

    [Fact]
    public void AClosedChange_IsNeverTakenAsAWait_EvenIfItCarriesAPrompt()
    {
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("s1", SessionChangeKind.Closed, Waiting(Start.AddSeconds(30)))));

        _alerts.Observe(Change("s2", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30))));
        Assert.Equal(NeedsInputAction.Dismiss, _alerts.Observe(Change("s2", SessionChangeKind.Closed, Waiting(Start.AddSeconds(30)))));
    }

    [Fact]
    public void ASessionThatReopensWaiting_AfterClosing_AlertsAgain()
    {
        _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30))));
        _alerts.Observe(Change("s1", SessionChangeKind.Closed));

        var action = _alerts.Observe(Change("s1", SessionChangeKind.Started, Waiting(Start.AddMinutes(10))));

        Assert.Equal(NeedsInputAction.Alert, action);
    }

    [Fact]
    public void Sessions_AreTrackedIndependently()
    {
        Assert.Equal(NeedsInputAction.Alert, _alerts.Observe(Change("a", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(30)))));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("b", SessionChangeKind.Started)));
        Assert.Equal(NeedsInputAction.Alert, _alerts.Observe(Change("b", SessionChangeKind.NeedsInputChanged, Waiting(Start.AddSeconds(45)))));

        Assert.Equal(NeedsInputAction.Dismiss, _alerts.Observe(Change("a", SessionChangeKind.NeedsInputChanged)));
        Assert.Equal(NeedsInputAction.None, _alerts.Observe(Change("b", SessionChangeKind.Finished, Waiting(Start.AddSeconds(45)))));
        Assert.Equal(NeedsInputAction.Dismiss, _alerts.Observe(Change("b", SessionChangeKind.Closed)));
    }

    [Fact]
    public void AWait_WithoutATimestamp_IsTakenAsRaisedWhenItWasObserved()
    {
        var undated = new PendingPrompt(PendingPromptKind.Permission, null);

        Assert.Equal(NeedsInputAction.Alert, _alerts.Observe(Change("s1", SessionChangeKind.NeedsInputChanged, undated, at: Start.AddMinutes(1))));
    }
}
