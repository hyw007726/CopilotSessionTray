using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class ResumeSummaryBuilderTests
{
    private const string SessionId = "0ec123d7-753e-4fef-977f-4eef290ff398";
    private const string TruncationMarker = " … (truncated)";
    private const string OldFormatHeader =
        "[Context: this is a condensed summary of an earlier Copilot CLI session, titled 'Old' (id: abc-123). " +
        "It is not itself part of this request — if the full original conversation is ever needed, " +
        "it can be resumed directly with `/resume abc-123`.]";

    private static string Paragraphs(params string[] parts) => string.Join(Environment.NewLine + Environment.NewLine, parts);

    private static string BodyOf(string seed) => seed[(seed.IndexOf(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal) + 4)..];

    [Fact]
    public void Build_UsesTheLatestCheckpointOverview_WhenOneExists()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, "Fix the tray icon",
            [Checkpoint(1, "older overview"), Checkpoint(2, "latest overview")],
            [Turn(0, "first question", "first answer")]);

        Assert.Equal("latest overview", BodyOf(result));
    }

    [Fact]
    public void Build_PrefixesAHeaderNamingTheOriginalSession()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, "Fix the tray icon", [Checkpoint(1, "overview")], null);

        Assert.StartsWith("[Context: this is a condensed summary of an earlier Copilot CLI session", result);
        Assert.Contains($"titled \"Fix the tray icon\" (id: {SessionId})", result);
        Assert.Contains($"`/resume {SessionId}`", result);
    }

    [Fact]
    public void Build_AsksTheNewAgentToAcknowledgeBrieflyAndWait_FromInsideTheHeader()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, null, [Checkpoint(1, "overview")], null);

        var header = result[..result.IndexOf(Environment.NewLine, StringComparison.Ordinal)];
        Assert.EndsWith("Please acknowledge in one short line and wait for my next message.]", header);
    }

    [Fact]
    public void Build_AppendsTheNextStepsOfTheLatestCheckpoint()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, null,
            [Checkpoint(1, "old overview", "old steps"), Checkpoint(2, "the overview", "run the tests, then push")],
            null);

        Assert.Equal(Paragraphs("the overview", "Next steps: run the tests, then push"), BodyOf(result));
    }

    [Fact]
    public void Build_OmitsTheNextStepsLabel_WhenTheCheckpointHasNone()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, null, [Checkpoint(1, "the overview", "  ")], null);

        Assert.Equal("the overview", BodyOf(result));
    }

    [Fact]
    public void Build_BoundsTheOverviewAndTheNextStepsSeparately()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, null, [Checkpoint(1, new string('o', 9_000), new string('n', 9_000))], null);

        var body = BodyOf(result);
        Assert.Equal(
            Paragraphs(
                new string('o', ResumeSummaryBuilder.MaxOverviewLength) + TruncationMarker,
                "Next steps: " + new string('n', ResumeSummaryBuilder.MaxNextStepsLength) + TruncationMarker),
            body);
        Assert.True(body.Length <= ResumeSummaryBuilder.MaxBodyLength, $"body was {body.Length} characters");
    }

    [Fact]
    public void Build_SkipsCheckpointsWithAnEmptyOverview_UsingTheLastNonEmptyOne()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, null, [Checkpoint(1, "usable overview"), Checkpoint(2, "   ")], null);

        Assert.Equal("usable overview", BodyOf(result));
    }

    [Fact]
    public void Build_TruncatesAnOversizedOverview_AtTheBodyBudget_WhenThereAreNoNextSteps()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, null, [Checkpoint(1, new string('o', 9_000))], null);

        Assert.Equal(new string('o', ResumeSummaryBuilder.MaxBodyLength) + TruncationMarker, BodyOf(result));
    }

    [Fact]
    public void Build_RecapsTheFirstAskAndLatestReply_WhenThereAreNoCheckpoints()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, "Tray icon", null,
            [Turn(0, "Why is the tray icon small?", "It was point-sampled."), Turn(1, "Fix it", "Fixed by drawing at the exact size.")]);

        Assert.Equal(
            Paragraphs("Originally asked: Why is the tray icon small?", "Most recent outcome: Fixed by drawing at the exact size."),
            BodyOf(result));
    }

    [Fact]
    public void Build_KeepsTheLatestOutcome_WhenTheFirstMessageIsHuge()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, "Automation run", null,
            [Turn(0, new string('x', 50_000), null), Turn(1, "follow up", "The fix is merged and the tests pass.")]);

        Assert.Contains("Most recent outcome: The fix is merged and the tests pass.", result);
        Assert.Contains("Originally asked: " + new string('x', ResumeSummaryBuilder.MaxRecapAskLength) + TruncationMarker, result);
    }

    [Fact]
    public void Build_BoundsTheWholeRecap_WhenBothPartsAreHuge()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, "Automation run", null, [Turn(0, new string('a', 50_000), new string('r', 50_000))]);

        var body = BodyOf(result);
        Assert.True(body.Length <= ResumeSummaryBuilder.MaxBodyLength, $"recap body was {body.Length} characters");
        Assert.Contains("Most recent outcome: " + new string('r', ResumeSummaryBuilder.MaxRecapOutcomeLength) + TruncationMarker, body);
    }

    [Fact]
    public void Build_UsesTheLastNonEmptyAssistantResponse_WhenTheFinalTurnHasNone()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, null, null,
            [Turn(0, "ask", "the last real reply"), Turn(1, "keep going", null), Turn(2, "and more", "  ")]);

        Assert.Contains("Most recent outcome: the last real reply", result);
    }

    [Fact]
    public void Build_OmitsTheOutcome_WhenNoTurnHasAResponse()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, null, null, [Turn(0, "only an ask", null)]);

        Assert.Contains("Originally asked: only an ask", result);
        Assert.DoesNotContain("Most recent outcome", result);
    }

    [Fact]
    public void Build_DoesNotQuoteAnEarlierProvenanceHeader_AsTheOriginalAsk()
    {
        var seed = ResumeSummaryBuilder.Build("11111111-aaaa-bbbb-cccc-222222222222", "Earlier session", [Checkpoint(1, "Earlier work: icon glow.")], null);

        var result = ResumeSummaryBuilder.Build(SessionId, "Resumed session", null, [Turn(0, seed, "Understood.")]);

        Assert.Contains("Originally asked: Earlier work: icon glow.", result);
        Assert.Equal(1, CountOccurrences(result, "[Context:"));
    }

    [Fact]
    public void Build_RecognisesAHeaderThatWasFlattenedAndMangledByTheOldLauncher()
    {
        var mangled = OldFormatHeader.Replace("—", "ÔÇö") + "    The real earlier summary.";

        var result = ResumeSummaryBuilder.Build(SessionId, null, null, [Turn(0, mangled, "Understood.")]);

        Assert.Contains("Originally asked: The real earlier summary.", result);
    }

    [Fact]
    public void Build_UsesTheNextMessage_WhenTheFirstOneIsOnlyAProvenanceHeader()
    {
        var result = ResumeSummaryBuilder.Build(
            SessionId, null, null, [Turn(0, OldFormatHeader, "Ready."), Turn(1, "Now fix the balloons", "Done.")]);

        Assert.Contains("Originally asked: Now fix the balloons", result);
    }

    [Fact]
    public void Build_FallsBackToTheTitle_WhenThereAreNoCheckpointsOrTurns()
    {
        Assert.Equal("Fix the tray icon", BodyOf(ResumeSummaryBuilder.Build(SessionId, "Fix the tray icon", [], [])));
        Assert.Equal("Fix the tray icon", BodyOf(ResumeSummaryBuilder.Build(SessionId, "Fix the tray icon", null, null)));
    }

    [Fact]
    public void Build_SaysNoSummaryIsAvailable_WhenThereIsNothingAtAll()
    {
        var result = ResumeSummaryBuilder.Build(SessionId, null, null, null);

        Assert.Equal("No summary is available for this session yet.", BodyOf(result));
    }

    [Fact]
    public void Build_KeepsTheHeaderTitleShortAndOnOneLine()
    {
        var longTitle = ResumeSummaryBuilder.Build(SessionId, new string('t', 500), [Checkpoint(1, "overview")], null);
        var multiLineTitle = ResumeSummaryBuilder.Build(SessionId, "first line\nsecond line", [Checkpoint(1, "overview")], null);

        var longHeader = longTitle[..longTitle.IndexOf(Environment.NewLine, StringComparison.Ordinal)];
        var multiLineHeader = multiLineTitle[..multiLineTitle.IndexOf(Environment.NewLine, StringComparison.Ordinal)];
        Assert.Contains("titled \"" + new string('t', 79) + "…\"", longHeader);
        Assert.Contains("titled \"first line second line\"", multiLineHeader);
    }

    [Fact]
    public void Truncate_LeavesTextWithinTheLimitUntouched()
    {
        Assert.Equal("abcd", ResumeSummaryBuilder.Truncate("abcd", 4));
    }

    [Fact]
    public void Truncate_DoesNotSplitASurrogatePair()
    {
        Assert.Equal("aaa" + TruncationMarker, ResumeSummaryBuilder.Truncate("aaa🙂bbb", 4));
    }

    [Fact]
    public void StripProvenanceHeader_RemovesTheCurrentAndTheOldHeaderFormats()
    {
        var current = ResumeSummaryBuilder.Build("abc-123", "Old", [Checkpoint(1, "body text")], null);

        Assert.Equal("body text", ResumeSummaryBuilder.StripProvenanceHeader(current));
        Assert.Equal("body text", ResumeSummaryBuilder.StripProvenanceHeader(OldFormatHeader + Environment.NewLine + Environment.NewLine + "body text"));
    }

    [Fact]
    public void StripProvenanceHeader_LeavesOrdinaryTextAlone()
    {
        Assert.Equal("Why is [Context: this] odd?", ResumeSummaryBuilder.StripProvenanceHeader("Why is [Context: this] odd?"));
        Assert.Null(ResumeSummaryBuilder.StripProvenanceHeader(null));
    }

    [Fact]
    public void FindOriginSessionId_ReadsTheIdFromAHeaderWrittenByBuild()
    {
        var seed = ResumeSummaryBuilder.Build("11111111-aaaa-bbbb-cccc-222222222222", "Earlier", [Checkpoint(1, "x")], null);

        Assert.Equal("11111111-aaaa-bbbb-cccc-222222222222", ResumeSummaryBuilder.FindOriginSessionId([Turn(0, seed, "ok")]));
    }

    [Fact]
    public void FindOriginSessionId_ReadsTheIdFromAnOldFormatHeader()
    {
        Assert.Equal("abc-123", ResumeSummaryBuilder.FindOriginSessionId([Turn(0, OldFormatHeader + "    body", "ok")]));
    }

    [Fact]
    public void FindOriginSessionId_ReturnsNull_ForOrdinarySessions()
    {
        Assert.Null(ResumeSummaryBuilder.FindOriginSessionId([Turn(0, "Please fix the build (id: not a header)", "ok")]));
        Assert.Null(ResumeSummaryBuilder.FindOriginSessionId([]));
        Assert.Null(ResumeSummaryBuilder.FindOriginSessionId(null));
    }

    [Fact]
    public void FindOriginSessionId_SkipsLeadingEmptyMessages()
    {
        Assert.Equal("abc-123", ResumeSummaryBuilder.FindOriginSessionId([Turn(0, "  ", null), Turn(1, OldFormatHeader, "ok")]));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static readonly DateTimeOffset WrittenAt = new(2026, 10, 9, 11, 20, 41, TimeSpan.Zero);

    private static string[] Texts(SessionSummaryView view) => view.Sections.Select(section => section.Text).ToArray();

    private static string[] Headings(SessionSummaryView view) => view.Sections.Select(section => section.Heading).ToArray();

    [Fact]
    public void BuildView_ForACheckpoint_HasOverviewAndNextStepsSections_WithTheCheckpointsNumberAndTime()
    {
        var checkpoint = new SessionCheckpoint(SessionId, 3, "Title", "the overview", WrittenAt, "- run the tests");

        var view = ResumeSummaryBuilder.BuildView("Title", [checkpoint], [Turn(0, "ignored", "ignored")]);

        Assert.Equal(SessionSummarySource.Checkpoint, view.Source);
        Assert.Equal(new[] { "Overview", "Next steps" }, Headings(view));
        Assert.Equal(new[] { "the overview", "- run the tests" }, Texts(view));
        Assert.Equal(3, view.CheckpointNumber);
        Assert.Equal(WrittenAt, view.WrittenAtUtc);
    }

    [Fact]
    public void BuildView_ForACheckpointWithoutNextSteps_HasOnlyTheOverview()
    {
        var view = ResumeSummaryBuilder.BuildView(null, [Checkpoint(1, "the overview", "  ")], null);

        Assert.Equal(new[] { "Overview" }, Headings(view));
        Assert.Equal(new[] { "the overview" }, Texts(view));
    }

    [Fact]
    public void BuildView_UsesTheLatestUsableCheckpoint()
    {
        var older = new SessionCheckpoint(SessionId, 1, null, "older overview", WrittenAt.AddHours(-3));
        var latest = new SessionCheckpoint(SessionId, 2, null, "latest overview", WrittenAt);
        var empty = new SessionCheckpoint(SessionId, 3, null, "  ", WrittenAt.AddHours(1));

        var view = ResumeSummaryBuilder.BuildView(null, [older, latest, empty], null);

        Assert.Equal(new[] { "latest overview" }, Texts(view));
        Assert.Equal(2, view.CheckpointNumber);
        Assert.Equal(WrittenAt, view.WrittenAtUtc);
    }

    [Fact]
    public void BuildView_BoundsTheSectionsExactlyLikeTheSeed()
    {
        var view = ResumeSummaryBuilder.BuildView(null, [Checkpoint(1, new string('o', 9_000), new string('n', 9_000))], null);

        Assert.Equal(
            new[]
            {
                new string('o', ResumeSummaryBuilder.MaxOverviewLength) + TruncationMarker,
                new string('n', ResumeSummaryBuilder.MaxNextStepsLength) + TruncationMarker,
            },
            Texts(view));
    }

    [Fact]
    public void BuildView_ForARecap_HasTheFirstRequestAndTheLatestReply()
    {
        var view = ResumeSummaryBuilder.BuildView(
            "Tray icon", null,
            [Turn(0, "Why is the tray icon small?", "It was point-sampled."), Turn(1, "Fix it", null), Turn(2, "Thanks", "Fixed by drawing at the exact size.")]);

        Assert.Equal(SessionSummarySource.Recap, view.Source);
        Assert.Equal(new[] { "First request", "Latest reply" }, Headings(view));
        Assert.Equal(new[] { "Why is the tray icon small?", "Fixed by drawing at the exact size." }, Texts(view));
        Assert.Null(view.CheckpointNumber);
        Assert.Null(view.WrittenAtUtc);
    }

    [Fact]
    public void BuildView_ForARecap_DropsAnEarlierProvenanceHeader_AndMissingReplies()
    {
        var seed = ResumeSummaryBuilder.Build("11111111-aaaa-bbbb-cccc-222222222222", "Earlier", [Checkpoint(1, "Earlier work.")], null);

        var view = ResumeSummaryBuilder.BuildView(null, null, [Turn(0, seed, null)]);

        Assert.Equal(new[] { "First request" }, Headings(view));
        Assert.Equal(new[] { "Earlier work." }, Texts(view));
    }

    [Fact]
    public void BuildView_ForARecap_BoundsTheSectionsExactlyLikeTheSeed()
    {
        var view = ResumeSummaryBuilder.BuildView(null, null, [Turn(0, new string('a', 50_000), new string('r', 50_000))]);

        Assert.Equal(
            new[]
            {
                new string('a', ResumeSummaryBuilder.MaxRecapAskLength) + TruncationMarker,
                new string('r', ResumeSummaryBuilder.MaxRecapOutcomeLength) + TruncationMarker,
            },
            Texts(view));
    }

    [Fact]
    public void BuildView_FallsBackToTheTitle_ThenToNothing()
    {
        var titleOnly = ResumeSummaryBuilder.BuildView("Fix the tray icon", [], []);
        var nothing = ResumeSummaryBuilder.BuildView("  ", null, null);

        Assert.Equal(SessionSummarySource.TitleOnly, titleOnly.Source);
        Assert.Equal(new[] { "Title" }, Headings(titleOnly));
        Assert.Equal(new[] { "Fix the tray icon" }, Texts(titleOnly));
        Assert.Equal(SessionSummarySource.None, nothing.Source);
        Assert.Empty(nothing.Sections);
    }

    public static TheoryData<string, SessionCheckpoint[], SessionTurn[]> SeedScenarios() => new()
    {
        { "checkpoint with next steps", [Checkpoint(1, "overview text", "next text")], [] },
        { "checkpoint without next steps", [Checkpoint(1, "overview text")], [Turn(0, "ask", "reply")] },
        { "oversized checkpoint", [Checkpoint(1, new string('o', 9_000), new string('n', 9_000))], [] },
        { "recap", [], [Turn(0, "the ask", "the reply")] },
        { "recap without a reply", [], [Turn(0, "only an ask", null)] },
        { "title only", [], [] },
    };

    [Theory]
    [MemberData(nameof(SeedScenarios))]
    public void BuildView_AndBuild_AgreeOnTheContent(string scenario, SessionCheckpoint[] checkpoints, SessionTurn[] turns)
    {
        var seed = ResumeSummaryBuilder.Build(SessionId, "A title", checkpoints, turns);
        var view = ResumeSummaryBuilder.BuildView("A title", checkpoints, turns);

        var body = BodyOf(seed);
        var position = 0;
        foreach (var text in Texts(view))
        {
            var found = body.IndexOf(text, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"{scenario}: section text was not found in order in the seed body");
            position = found + text.Length;
        }

        if (view.Sections.Count == 0)
        {
            Assert.Equal("No summary is available for this session yet.", body);
        }
    }

    private static SessionCheckpoint Checkpoint(int number, string? overview, string? nextSteps = null) =>
        new(SessionId, number, Title: null, overview, DateTimeOffset.UnixEpoch, nextSteps);

    private static SessionTurn Turn(int index, string? userMessage, string? assistantResponse) =>
        new(SessionId, index, userMessage, assistantResponse, DateTimeOffset.UnixEpoch);
}
