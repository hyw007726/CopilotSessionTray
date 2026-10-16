using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionGistTests
{
    private static SessionSummaryView View(SessionSummarySource source, params string[] sectionTexts) =>
        new(source, sectionTexts.Select((text, index) => new SessionSummarySection($"Section {index}", text)).ToList());

    [Fact]
    public void Snippet_FlattensMarkdownToOnePlainLine()
    {
        Assert.Equal("fixed it tests pass", SessionGist.Snippet("## Done\n- fixed **it**\n- `tests` pass"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("```\ncode only\n```")]
    public void Snippet_ReturnsNull_WhenNothingReadableIsLeft(string? text)
    {
        Assert.Null(SessionGist.Snippet(text));
    }

    [Fact]
    public void Snippet_CutsLongTextToTheLimit_WithAnEllipsis()
    {
        var snippet = SessionGist.Snippet(new string('a', 1000) + " tail");

        Assert.Equal(SessionGist.MaxLength, snippet!.Length);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Choose_PrefersTheRequest_WhileTheSessionWorks()
    {
        Assert.Equal("do the thing", SessionGist.Choose(SessionStatus.Working, "do the thing", "old outcome"));
    }

    [Theory]
    [InlineData(SessionStatus.WaitingForInput)]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Closed)]
    public void Choose_PrefersTheOutcome_WhenTheSessionIsNotWorking(SessionStatus status)
    {
        Assert.Equal("all done", SessionGist.Choose(status, "do the thing", "all done"));
    }

    [Fact]
    public void Choose_FallsBackToWhicheverIsKnown()
    {
        Assert.Equal("old outcome", SessionGist.Choose(SessionStatus.Working, null, "old outcome"));
        Assert.Equal("do the thing", SessionGist.Choose(SessionStatus.Finished, "do the thing", "  "));
        Assert.Null(SessionGist.Choose(SessionStatus.Working, null, null));
    }

    [Fact]
    public void FromView_UsesTheLatestReply_OfARecap()
    {
        var view = View(SessionSummarySource.Recap, "Fix the build", "Fixed **it**.");

        Assert.Equal("Fixed it.", SessionGist.FromView(view));
    }

    [Fact]
    public void FromView_UsesTheFirstRequest_WhenARecapHasNoReplyYet()
    {
        Assert.Equal("Fix the build", SessionGist.FromView(View(SessionSummarySource.Recap, "Fix the build")));
    }

    [Fact]
    public void FromView_UsesTheOverview_OfACheckpoint()
    {
        var view = View(SessionSummarySource.Checkpoint, "Rebuilt the retry policy.", "- add tests");

        Assert.Equal("Rebuilt the retry policy.", SessionGist.FromView(view));
    }

    [Fact]
    public void FromView_HasNothing_ForATitleOnlyEmptyOrMissingView()
    {
        Assert.Null(SessionGist.FromView(View(SessionSummarySource.TitleOnly, "Just a title")));
        Assert.Null(SessionGist.FromView(View(SessionSummarySource.None)));
        Assert.Null(SessionGist.FromView(null));
    }
}
