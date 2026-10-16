using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionSummaryViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SessionSummaryView Checkpoint(int? number, DateTimeOffset? writtenAt) =>
        new(SessionSummarySource.Checkpoint, [new SessionSummarySection("Overview", "text")], number, writtenAt);

    [Fact]
    public void Describe_NamesTheCheckpointAndHowLongAgoItWasWritten()
    {
        Assert.Equal("Checkpoint 2 · 38m ago", Checkpoint(2, Now.AddMinutes(-38)).Describe(Now));
    }

    [Theory]
    [InlineData(-20, "Checkpoint 2 · just now")]
    [InlineData(-59, "Checkpoint 2 · just now")]
    [InlineData(-60, "Checkpoint 2 · 1m ago")]
    [InlineData(300, "Checkpoint 2 · just now")]
    public void Describe_SaysJustNow_ForAgesUnderAMinute_OrInTheFuture(int secondsFromNow, string expected)
    {
        Assert.Equal(expected, Checkpoint(2, Now.AddSeconds(secondsFromNow)).Describe(Now));
    }

    [Fact]
    public void Describe_UsesTheLargestUnitsForOlderCheckpoints()
    {
        Assert.Equal("Checkpoint 5 · 1h 5m ago", Checkpoint(5, Now.AddMinutes(-65)).Describe(Now));
        Assert.Equal("Checkpoint 5 · 1d 2h ago", Checkpoint(5, Now.AddHours(-26).AddMinutes(-5)).Describe(Now));
    }

    [Fact]
    public void Describe_CopesWithAMissingNumberOrTime()
    {
        Assert.Equal("Latest checkpoint · 10m ago", Checkpoint(null, Now.AddMinutes(-10)).Describe(Now));
        Assert.Equal("Checkpoint 3", Checkpoint(3, null).Describe(Now));
        Assert.Equal("Latest checkpoint", Checkpoint(null, null).Describe(Now));
    }

    [Fact]
    public void Describe_NamesTheSectionsARecapActuallyHas()
    {
        var both = new SessionSummaryView(
            SessionSummarySource.Recap, [new SessionSummarySection("First request", "a"), new SessionSummarySection("Latest reply", "b")]);
        var askOnly = new SessionSummaryView(SessionSummarySource.Recap, [new SessionSummarySection("First request", "a")]);

        Assert.Equal("No checkpoint yet · recap of the first request and the latest reply", both.Describe(Now));
        Assert.Equal("No checkpoint yet · recap of the first request", askOnly.Describe(Now));
    }

    [Theory]
    [InlineData(SessionSummarySource.Recap, "No checkpoint yet")]
    [InlineData(SessionSummarySource.TitleOnly, "only the title")]
    [InlineData(SessionSummarySource.None, "Nothing to summarize")]
    public void Describe_ExplainsWhyThereIsNoCheckpoint(SessionSummarySource source, string expectedFragment)
    {
        var view = new SessionSummaryView(source, []);

        Assert.Contains(expectedFragment, view.Describe(Now));
        Assert.DoesNotContain("Checkpoint 1", view.Describe(Now));
    }

    [Fact]
    public void Sources_AreOrderedFromWorstToBest_SoARefreshCanTellADowngrade()
    {
        Assert.True(SessionSummarySource.None < SessionSummarySource.TitleOnly);
        Assert.True(SessionSummarySource.TitleOnly < SessionSummarySource.Recap);
        Assert.True(SessionSummarySource.Recap < SessionSummarySource.Checkpoint);
    }
}
