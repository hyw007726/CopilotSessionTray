using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionTextTests
{
    [Theory]
    [InlineData("plain title", 50, "plain title")]
    [InlineData("  spaced \t\r\n out  ", 50, "spaced out")]
    [InlineData("line one\nline two", 50, "line one line two")]
    [InlineData("line one\r\n\r\nline two", 50, "line one line two")]
    [InlineData("", 10, "")]
    [InlineData("   \r\n  ", 10, "")]
    public void ToSingleLine_CollapsesWhitespaceAndTrims(string input, int maxLength, string expected)
    {
        Assert.Equal(expected, SessionText.ToSingleLine(input, maxLength));
    }

    [Fact]
    public void ToSingleLine_ShortensWithAnEllipsis_WithinTheLimit()
    {
        var result = SessionText.ToSingleLine("abcdefghij", 6);

        Assert.Equal("abcde…", result);
    }

    [Fact]
    public void ToSingleLine_LeavesTextAtExactlyTheLimitUntouched()
    {
        Assert.Equal("abcdef", SessionText.ToSingleLine("abcdef", 6));
    }

    [Fact]
    public void ToSingleLine_DoesNotSplitASurrogatePair()
    {
        var result = SessionText.ToSingleLine("aa🙂bbb", 4);

        Assert.Equal("aa…", result);
    }

    [Fact]
    public void ToSingleLine_HandlesVeryLongInput()
    {
        var result = SessionText.ToSingleLine(new string('x', 2_000_000) + "\n" + new string('y', 10), 200);

        Assert.Equal(200, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void ToSingleLine_RejectsALimitTooSmallForAnEllipsis()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionText.ToSingleLine("abc", 1));
    }

    [Theory]
    [InlineData("**Fixed** the flaky test.\n\n- Updated `retry.ts`\n- Added a test", "Fixed the flaky test. Updated retry.ts Added a test")]
    [InlineData("## Summary\nAll tests pass.", "All tests pass.")]
    [InlineData("## Summary", "Summary")]
    [InlineData("> quoted line\n1. first\n2) second\n* third\n+ fourth", "quoted line first second third fourth")]
    [InlineData("See [the docs](https://example.com/x) for more", "See the docs for more")]
    [InlineData("Use snake_case and a * b kept as is", "Use snake_case and a * b kept as is")]
    [InlineData("Done.\n```\nnpm test\n```\nAll green", "Done. All green")]
    [InlineData("Done.\r\n\r\n   Next:   deploy  ", "Done. Next: deploy")]
    [InlineData("", "")]
    [InlineData("  \n ``` \n code \n```\n", "")]
    public void ToPlainSnippet_TurnsMarkdownIntoOneLineOfPlainText(string markdown, string expected)
    {
        Assert.Equal(expected, SessionText.ToPlainSnippet(markdown, 200));
    }

    [Fact]
    public void ToPlainSnippet_ShortensWithAnEllipsis()
    {
        var result = SessionText.ToPlainSnippet("**" + new string('a', 50) + "** and then some more text", 20);

        Assert.Equal(new string('a', 19) + "…", result);
    }

    [Fact]
    public void FormatDuration_UsesTheLargestSensibleUnits()
    {
        Assert.Equal("45s", SessionText.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("12m", SessionText.FormatDuration(TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(50))));
        Assert.Equal("1h 5m", SessionText.FormatDuration(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(5))));
        Assert.Equal("2d 3h", SessionText.FormatDuration(TimeSpan.FromDays(2).Add(TimeSpan.FromHours(3))));
        Assert.Equal("0s", SessionText.FormatDuration(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void FormatDuration_SwitchesUnitsExactlyAtTheBoundaries()
    {
        Assert.Equal("59s", SessionText.FormatDuration(TimeSpan.FromSeconds(59)));
        Assert.Equal("1m", SessionText.FormatDuration(TimeSpan.FromSeconds(60)));
        Assert.Equal("59m", SessionText.FormatDuration(TimeSpan.FromSeconds(3599)));
        Assert.Equal("1h 0m", SessionText.FormatDuration(TimeSpan.FromHours(1)));
        Assert.Equal("23h 59m", SessionText.FormatDuration(TimeSpan.FromMinutes((24 * 60) - 1)));
        Assert.Equal("1d 0h", SessionText.FormatDuration(TimeSpan.FromDays(1)));
    }
}
