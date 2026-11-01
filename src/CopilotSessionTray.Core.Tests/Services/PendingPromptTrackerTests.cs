using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class PendingPromptTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static SessionEventRecord Event(string type, string dataJson, DateTimeOffset at) =>
        new(SessionEventKind.Unknown, type, at, $"{{\"type\":\"{type}\",\"timestamp\":\"{at:O}\",\"data\":{dataJson}}}");

    private static SessionEventRecord PermissionRequested(string id, DateTimeOffset at) =>
        Event("permission.requested", $"{{\"requestId\":\"{id}\",\"permissionRequest\":{{\"kind\":\"shell\"}}}}", at);

    private static SessionEventRecord PermissionCompleted(string id, DateTimeOffset at) =>
        Event("permission.completed", $"{{\"requestId\":\"{id}\",\"result\":{{\"kind\":\"approved\"}}}}", at);

    private static SessionEventRecord ToolStarted(string tool, string callId, DateTimeOffset at) =>
        Event("tool.execution_start", $"{{\"toolCallId\":\"{callId}\",\"toolName\":\"{tool}\",\"arguments\":{{}}}}", at);

    private static SessionEventRecord ToolCompleted(string callId, DateTimeOffset at) =>
        Event("tool.execution_complete", $"{{\"toolCallId\":\"{callId}\",\"success\":true}}", at);

    private static PendingPrompt After(params SessionEventRecord[] events)
    {
        var tracker = new PendingPromptTracker();
        foreach (var record in events)
        {
            tracker.Observe(record);
        }

        return tracker.Current;
    }

    [Fact]
    public void ASessionWithoutEvents_IsNotWaiting()
    {
        var prompt = After();

        Assert.False(prompt.IsPending);
        Assert.Equal(PendingPromptKind.None, prompt.Kind);
        Assert.Null(prompt.SinceUtc);
    }

    [Fact]
    public void APermissionRequest_IsPendingUntilItsCompletion()
    {
        var tracker = new PendingPromptTracker();

        tracker.Observe(PermissionRequested("req-1", T0));
        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0), tracker.Current);

        tracker.Observe(PermissionCompleted("req-1", T0.AddSeconds(40)));
        Assert.False(tracker.Current.IsPending);
    }

    [Fact]
    public void ACompletion_OfAnotherRequest_DoesNotAnswerThisOne()
    {
        var prompt = After(PermissionRequested("req-1", T0), PermissionCompleted("req-2", T0.AddSeconds(5)));

        Assert.Equal(PendingPromptKind.Permission, prompt.Kind);
    }

    [Fact]
    public void SeveralRequests_StayPendingUntilEachIsAnswered_AndTheOldestRemainingSetsTheTime()
    {
        var tracker = new PendingPromptTracker();
        tracker.Observe(PermissionRequested("req-1", T0));
        tracker.Observe(PermissionRequested("req-2", T0.AddSeconds(30)));

        tracker.Observe(PermissionCompleted("req-2", T0.AddSeconds(40)));
        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0), tracker.Current);

        tracker.Observe(PermissionRequested("req-3", T0.AddSeconds(50)));
        tracker.Observe(PermissionCompleted("req-1", T0.AddSeconds(60)));
        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0.AddSeconds(50)), tracker.Current);

        tracker.Observe(PermissionCompleted("req-3", T0.AddSeconds(70)));
        Assert.False(tracker.Current.IsPending);
    }

    [Theory]
    [InlineData("ask_user")]
    [InlineData("exit_plan_mode")]
    public void AQuestionOrAPlanApproval_IsPendingUntilItsToolCallCompletes(string tool)
    {
        var tracker = new PendingPromptTracker();

        tracker.Observe(ToolStarted(tool, "call-1", T0));
        Assert.Equal(new PendingPrompt(PendingPromptKind.Question, T0), tracker.Current);

        tracker.Observe(ToolCompleted("call-1", T0.AddMinutes(3)));
        Assert.False(tracker.Current.IsPending);
    }

    [Fact]
    public void TheCompletionOfAnotherToolCall_DoesNotAnswerAQuestion()
    {
        var prompt = After(ToolStarted("ask_user", "call-1", T0), ToolStarted("view", "call-2", T0.AddSeconds(1)), ToolCompleted("call-2", T0.AddSeconds(2)));

        Assert.Equal(PendingPromptKind.Question, prompt.Kind);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("view")]
    [InlineData("task")]
    public void OrdinaryToolCalls_AreNotPrompts_EvenWhileTheyRun(string tool)
    {
        Assert.False(After(ToolStarted(tool, "call-1", T0)).IsPending);
    }

    [Fact]
    public void AToolCallThatMerelyMentionsAskUser_IsNotAQuestion()
    {
        var escaped = Event("tool.execution_start", "{\"toolCallId\":\"call-1\",\"toolName\":\"powershell\",\"arguments\":{\"command\":\"echo \\\"ask_user\\\"\"}}", T0);
        var asValues = Event("tool.execution_start", "{\"toolCallId\":\"call-2\",\"toolName\":\"powershell\",\"arguments\":{\"note\":\"ask_user\",\"plan\":\"exit_plan_mode\"}}", T0);

        Assert.False(After(escaped, asValues).IsPending);
    }

    [Fact]
    public void WhenBothArePending_ThePermissionRequestIsReported_WithTheOldestTimeOfEither()
    {
        var prompt = After(ToolStarted("ask_user", "call-1", T0), PermissionRequested("req-1", T0.AddSeconds(20)));

        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0), prompt);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("session.shutdown")]
    [InlineData("session.resume")]
    [InlineData("session.start")]
    public void AnAbortShutdownResumeOrStart_DropsEverythingThatWasPending(string type)
    {
        var tracker = new PendingPromptTracker();
        tracker.Observe(PermissionRequested("req-1", T0));
        tracker.Observe(ToolStarted("ask_user", "call-1", T0.AddSeconds(1)));

        tracker.Observe(Event(type, "{}", T0.AddSeconds(5)));

        Assert.False(tracker.Current.IsPending);
    }

    [Fact]
    public void ARequestAfterAnAbort_IsPendingAgain()
    {
        var prompt = After(PermissionRequested("req-1", T0), Event("abort", "{\"reason\":\"user_abort\"}", T0.AddSeconds(5)), PermissionRequested("req-2", T0.AddSeconds(30)));

        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0.AddSeconds(30)), prompt);
    }

    [Fact]
    public void ACompletion_ArrivingAfterAnAbortHadAlreadyDroppedTheRequest_ChangesNothing()
    {
        var prompt = After(PermissionRequested("req-1", T0), Event("abort", "{}", T0.AddSeconds(5)), PermissionCompleted("req-1", T0.AddSeconds(6)));

        Assert.False(prompt.IsPending);
    }

    [Fact]
    public void EventsWithoutAnId_OrThatAreNotJson_AreIgnored()
    {
        var noId = Event("permission.requested", "{\"permissionRequest\":{\"kind\":\"shell\"}}", T0);
        var emptyId = Event("permission.requested", "{\"requestId\":\"\"}", T0);
        var notJson = new SessionEventRecord(SessionEventKind.Unknown, "permission.requested", T0, "{not json");
        var notAnObject = new SessionEventRecord(SessionEventKind.Unknown, "permission.requested", T0, "[1,2]");
        var noData = new SessionEventRecord(SessionEventKind.Unknown, "permission.requested", T0, "{\"type\":\"permission.requested\"}");
        var numericId = Event("permission.requested", "{\"requestId\":42}", T0);
        var questionWithoutCall = Event("tool.execution_start", "{\"toolName\":\"ask_user\"}", T0);

        Assert.False(After(noId, emptyId, notJson, notAnObject, noData, numericId, questionWithoutCall).IsPending);
    }

    [Fact]
    public void ARequestWithoutATimestamp_IsStillPending_WithoutASinceTime()
    {
        var request = new SessionEventRecord(SessionEventKind.Unknown, "permission.requested", null, "{\"type\":\"permission.requested\",\"data\":{\"requestId\":\"req-1\"}}");

        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, null), After(request));
    }

    [Fact]
    public void ARequestWithoutATimestamp_DoesNotHideTheTimeOfOneThatHasIt()
    {
        var undated = new SessionEventRecord(SessionEventKind.Unknown, "permission.requested", null, "{\"type\":\"permission.requested\",\"data\":{\"requestId\":\"req-1\"}}");

        var prompt = After(undated, PermissionRequested("req-2", T0));

        Assert.Equal(new PendingPrompt(PendingPromptKind.Permission, T0), prompt);
    }

    [Fact]
    public void UnrelatedEvents_ChangeNothing()
    {
        var tracker = new PendingPromptTracker();
        tracker.Observe(PermissionRequested("req-1", T0));

        foreach (var type in new[] { "assistant.turn_start", "assistant.message", "user.message", "session.usage_checkpoint", "hook.start", "system.notification" })
        {
            tracker.Observe(Event(type, "{\"requestId\":\"req-1\",\"toolCallId\":\"call-1\"}", T0.AddSeconds(1)));
        }

        Assert.Equal(PendingPromptKind.Permission, tracker.Current.Kind);
    }
}
