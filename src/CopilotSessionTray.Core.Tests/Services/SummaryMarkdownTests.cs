using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SummaryMarkdownTests
{
    private static string Dump(string? markdown) => string.Join(" | ", SummaryMarkdown.Parse(markdown).Select(DumpBlock));

    private static string DumpBlock(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => $"P({DumpRuns(paragraph.Runs)})",
        MarkdownHeading heading => $"H{heading.Level}({DumpRuns(heading.Runs)})",
        MarkdownList list => DumpList(list),
        MarkdownCodeBlock code => $"CODE({code.Text.Replace("\n", "¶")})",
        _ => throw new InvalidOperationException($"Unexpected block {block}"),
    };

    private static string DumpList(MarkdownList list) =>
        (list.Ordered ? $"OL{list.Start}" : "UL") + "{" +
        string.Join("; ", list.Items.Select(item =>
            DumpRuns(item.Runs) + (item.Children.Count > 0 ? " " + string.Join(" ", item.Children.Select(DumpList)) : string.Empty))) +
        "}";

    private static string DumpRuns(IReadOnlyList<MarkdownRun> runs) =>
        string.Concat(runs.Select(run =>
        {
            var flags = (run.Bold ? "b" : string.Empty) + (run.Italic ? "i" : string.Empty) + (run.Code ? "c" : string.Empty);
            return flags.Length == 0 ? run.Text : $"[{flags}:{run.Text}]";
        })).Replace("\n", "¶");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n ")]
    public void Parse_ReturnsNothing_ForEmptyInput(string? markdown)
    {
        Assert.Empty(SummaryMarkdown.Parse(markdown));
    }

    [Fact]
    public void Paragraphs_AreSeparatedByBlankLines()
    {
        Assert.Equal("P(one) | P(two)", Dump("one\n\n\n  \ntwo"));
    }

    [Fact]
    public void LineBreaksInsideAParagraph_AreKept()
    {
        Assert.Equal("P(Status: done¶Next: ship)", Dump("Status: done\nNext: ship"));
    }

    [Fact]
    public void WindowsAndClassicMacLineEndings_AreNormalised()
    {
        Assert.Equal("P(a¶b) | P(c)", Dump("a\r\nb\r\n\r\nc"));
        Assert.Equal("P(a¶b)", Dump("a\rb"));
    }

    [Fact]
    public void BoldAndCode_AreRecognisedInline()
    {
        Assert.Equal("P(Use [b:bold] and [c:code] here)", Dump("Use **bold** and `code` here"));
    }

    [Fact]
    public void CodeInsideBold_KeepsBothStyles()
    {
        Assert.Equal("P([b:run ][bc:x][b: now])", Dump("**run `x` now**"));
    }

    [Fact]
    public void BoldMarkersInsideCode_AreLiteral()
    {
        Assert.Equal("P([c:**not bold**] and [b:bold])", Dump("`**not bold**` and **bold**"));
    }

    [Fact]
    public void DoubleBacktickSpans_CanContainASingleBacktick()
    {
        Assert.Equal("P([c:a`b])", Dump("``a`b``"));
    }

    [Fact]
    public void CodeSpans_DropOneSurroundingSpacePerSide()
    {
        Assert.Equal("P([c:`tick`])", Dump("`` `tick` ``"));
        Assert.Equal("P([c: x])", Dump("`  x `"));
        Assert.Equal("P([c:  ])", Dump("`  `"));
    }

    [Theory]
    [InlineData("a ** b", "P(a ** b)")]
    [InlineData("**open only", "P(**open only)")]
    [InlineData("close only**", "P(close only**)")]
    [InlineData("`open only", "P(`open only)")]
    [InlineData("2 ** 3 and 4 ** 5", "P(2 ** 3 and 4 ** 5)")]
    [InlineData("**a** ** b", "P([b:a] ** b)")]
    [InlineData("**open text ** and more", "P(**open text ** and more)")]
    [InlineData("a ** b**", "P(a ** b**)")]
    public void UnmatchedOrSpaceFlankedMarkers_StayLiteral(string markdown, string expected)
    {
        Assert.Equal(expected, Dump(markdown));
    }

    [Fact]
    public void BackticksOfDifferentLengths_DoNotCloseEachOther()
    {
        Assert.Equal("P([c:one `` two] three)", Dump("`one `` two` three"));
        Assert.Equal("P([c:a] and [c:b])", Dump("`a` and ``b``"));
    }

    [Fact]
    public void BoldSpansMultipleWords_AndNeighbouringBoldRuns()
    {
        Assert.Equal("P([b:two words] and [b:more])", Dump("**two words** and **more**"));
    }

    [Fact]
    public void Italics_AreRecognisedWithSingleAsterisks()
    {
        Assert.Equal("P(this is [i:one] word and [i:a few words] too)", Dump("this is *one* word and *a few words* too"));
    }

    [Fact]
    public void ItalicsAndBold_CanNestInsideEachOther()
    {
        Assert.Equal("P([b:bold ][bi:and italic][b: text])", Dump("**bold *and italic* text**"));
        Assert.Equal("P([i:italic ][bi:and bold][i: text])", Dump("*italic **and bold** text*"));
        Assert.Equal("P([ic:code])", Dump("*`code`*"));
    }

    [Fact]
    public void ItalicsWorkInsideHeadingsAndListItems()
    {
        Assert.Equal("H2(A [i:big] title) | UL{an [i:emphasised] item}", Dump("## A *big* title\n- an *emphasised* item"));
    }

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("a*b*c")]
    [InlineData("5*3 = 15")]
    [InlineData("wildcards like *.cs and *.md")]
    [InlineData("a * b")]
    [InlineData("trailing star*")]
    [InlineData("*unclosed star")]
    public void AsterisksThatAreNotEmphasis_StayLiteral(string markdown)
    {
        Assert.Equal($"P({markdown})", Dump(markdown));
    }

    [Fact]
    public void ALoneStarAtTheStartOfALine_IsABullet_NotEmphasis()
    {
        Assert.Equal("UL{spaced *}", Dump("* spaced *"));
    }

    [Fact]
    public void RunsOfThreeOrMoreAsterisks_StayLiteral()
    {
        Assert.Equal("P(a ***b*** c)", Dump("a ***b*** c"));
        Assert.Equal("P(**** x)", Dump("**** x"));
    }

    [Fact]
    public void Headings_AreRecognisedWithTheirLevel()
    {
        Assert.Equal("H1(Title) | H2(Sub) | P(text)", Dump("# Title\n## Sub ##\ntext"));
        Assert.Equal("H3([b:Bold] heading)", Dump("### **Bold** heading"));
    }

    [Fact]
    public void AHashWithoutASpace_IsNotAHeading()
    {
        Assert.Equal("P(#hashtag)", Dump("#hashtag"));
        Assert.Equal("P(####### seven)", Dump("####### seven"));
    }

    [Fact]
    public void Bullets_AcceptEveryCommonMarker()
    {
        Assert.Equal("UL{a; b; c; d; e}", Dump("- a\n- b\n* c\n+ d\n• e"));
    }

    [Fact]
    public void NumberedLists_KeepTheirStartingNumber()
    {
        Assert.Equal("OL1{a; b}", Dump("1. a\n2) b"));
        Assert.Equal("OL3{x; y}", Dump("3. x\n4. y"));
    }

    [Fact]
    public void ChangingTheMarkerType_StartsANewList()
    {
        Assert.Equal("UL{a} | OL1{b}", Dump("- a\n1. b"));
    }

    [Fact]
    public void IndentedItems_NestUnderTheItemAboveThem()
    {
        Assert.Equal("UL{a UL{b; c}; d}", Dump("- a\n  - b\n  - c\n- d"));
    }

    [Fact]
    public void NestedListsMayMixNumberedAndBulleted()
    {
        Assert.Equal("OL1{step UL{detail}; next}", Dump("1. step\n   - detail\n2. next"));
    }

    [Fact]
    public void DeeplyNestedItems_KeepTheirStructure_AndDedentCleanly()
    {
        Assert.Equal("UL{a UL{b UL{c}; d}; e}", Dump("- a\n  - b\n    - c\n  - d\n- e"));
    }

    [Fact]
    public void AListThatStartsIndented_IsStillATopLevelList()
    {
        Assert.Equal("UL{a; b} | UL{c}", Dump("    - a\n    - b\n- c"));
    }

    [Fact]
    public void ListsContinueAcrossBlankLines()
    {
        Assert.Equal("UL{a; b}", Dump("- a\n\n- b"));
    }

    [Fact]
    public void AnIndentedLineAfterAnItem_ContinuesThatItem()
    {
        Assert.Equal("UL{a more words; b}", Dump("- a\n  more words\n- b"));
    }

    [Fact]
    public void AnUnindentedLineAfterAList_StartsAParagraph()
    {
        Assert.Equal("UL{a} | P(plain)", Dump("- a\nplain"));
        Assert.Equal("UL{a} | P(after a gap)", Dump("- a\n\nafter a gap"));
    }

    [Fact]
    public void AnIndentedLineAfterABlankLine_IsNotAContinuationOfTheItem()
    {
        Assert.Equal("UL{a} | P(indented after a gap)", Dump("- a\n\n  indented after a gap"));
    }

    [Fact]
    public void EmptyListItems_AreDropped()
    {
        Assert.Equal("UL{a; b}", Dump("- a\n-\n- b"));
    }

    [Fact]
    public void BoldThatMerelyStartsALine_IsNotABullet()
    {
        Assert.Equal("P([b:Note:] text)", Dump("**Note:** text"));
        Assert.Equal("UL{[b:Bold] item}", Dump("* **Bold** item"));
    }

    [Theory]
    [InlineData("2026 was a good year")]
    [InlineData("3.5 is a number")]
    [InlineData("-1 is negative")]
    [InlineData("1234. not a list")]
    public void LinesThatLookNumericButAreNotMarkers_StayParagraphs(string line)
    {
        Assert.Equal($"P({line})", Dump(line));
    }

    [Fact]
    public void FencedCode_KeepsItsContentVerbatim()
    {
        Assert.Equal(
            "P(before) | CODE(var x = 1;¶  indented) | P(after)",
            Dump("before\n```csharp\nvar x = 1;\n  indented\n```\nafter"));
    }

    [Fact]
    public void FencedCode_IsNotParsedForMarkdown()
    {
        Assert.Equal("CODE(**x**¶- y¶# z)", Dump("```\n**x**\n- y\n# z\n```"));
    }

    [Fact]
    public void AnUnterminatedFence_RunsToTheEndOfTheText()
    {
        Assert.Equal("P(intro) | CODE(line one¶line two)", Dump("intro\n```\nline one\nline two"));
    }

    [Fact]
    public void TildeFences_AndLongerFences_Work()
    {
        Assert.Equal("CODE(a)", Dump("~~~\na\n~~~"));
        Assert.Equal("CODE(```inner```)", Dump("````\n```inner```\n````"));
    }

    [Fact]
    public void AFenceIsOnlyClosedByALineOfTheSameKindAndAtLeastTheSameLength()
    {
        Assert.Equal("CODE(a¶```¶b)", Dump("````\na\n```\nb\n````"));
    }

    [Fact]
    public void TripleBackticksOnASingleLine_AreInlineCode_NotAFence()
    {
        Assert.Equal("P([c:code] here)", Dump("```code``` here"));
    }

    [Fact]
    public void FenceContent_LosesOnlyTheFencesOwnIndentation()
    {
        Assert.Equal("CODE(a¶  b)", Dump("  ```\n  a\n    b\n  ```"));
    }

    [Fact]
    public void TrailingBlankLinesInsideAFence_AreDropped()
    {
        Assert.Equal("CODE(a)", Dump("```\na\n\n\n```"));
    }

    [Fact]
    public void AnEmptyFence_ProducesNothing()
    {
        Assert.Equal("P(x) | P(y)", Dump("x\n```\n```\ny"));
    }

    [Fact]
    public void Tables_BecomeMonospaceBlocks_WithoutTheirSeparatorRow()
    {
        Assert.Equal("CODE(| a | b |¶| 1 | 2 |)", Dump("| a | b |\n|---|---|\n| 1 | 2 |"));
        Assert.Equal("CODE(| a | b |)", Dump("| a | b |\n| :--- | ---: |"));
    }

    [Fact]
    public void ThematicBreaks_AreDropped()
    {
        Assert.Equal("P(a) | P(b)", Dump("a\n\n---\n\nb"));
        Assert.Equal("P(a) | P(b)", Dump("a\n***\nb"));
        Assert.Equal("P(a) | P(b)", Dump("a\n- - -\nb"));
    }

    [Fact]
    public void BlockQuotes_LoseTheirMarker()
    {
        Assert.Equal("P(quoted¶more)", Dump("> quoted\n>> more"));
    }

    [Fact]
    public void Underscores_AreLeftAlone_AndALoneStarStaysLiteral()
    {
        Assert.Equal("P(snake_case_name and _x_ and [i:star] and a * b)", Dump("snake_case_name and _x_ and *star* and a * b"));
    }

    [Fact]
    public void RealisticCheckpointNextSteps_ParseIntoAReadableStructure()
    {
        const string markdown =
            "Remaining work:\n" +
            "- Finish `verify-picks`: re-run the **real-app** check.\n" +
            "  - Build `$texts` with a loop.\n" +
            "- `docs-picks`: update README.\n" +
            "\n" +
            "1. Run all tests (expect 188).\n" +
            "2. Relaunch the app.";

        Assert.Equal(
            "P(Remaining work:) | " +
            "UL{Finish [c:verify-picks]: re-run the [b:real-app] check. UL{Build [c:$texts] with a loop.}; [c:docs-picks]: update README.} | " +
            "OL1{Run all tests (expect 188).; Relaunch the app.}",
            Dump(markdown));
    }

    [Fact]
    public void PlainText_DropsEveryMarker_ButKeepsEveryWord()
    {
        const string markdown = "# Title\n\nSome **bold** and `code`.\n\n- one\n  - two\n1. three\n\n```\nraw  text\n```\n| a | b |";

        Assert.Equal("Title\nSome bold and code.\none\ntwo\nthree\nraw  text\n| a | b |", SummaryMarkdown.ToPlainText(SummaryMarkdown.Parse(markdown)));
    }

    [Fact]
    public void VeryDeepNesting_IsFlattenedBeyondAFixedDepth_WithoutLosingItems()
    {
        var markdown = string.Join("\n", Enumerable.Range(0, 2_000).Select(i => new string(' ', i) + "- item " + i));

        var blocks = SummaryMarkdown.Parse(markdown);

        var list = Assert.IsType<MarkdownList>(Assert.Single(blocks));
        Assert.True(Depth(list) <= SummaryMarkdown.MaxListDepth, $"depth was {Depth(list)}");
        Assert.Equal(2_000, SummaryMarkdown.ToPlainText(blocks).Split('\n').Length);
    }

    private static int Depth(MarkdownList list) =>
        1 + list.Items.SelectMany(item => item.Children).Select(Depth).DefaultIfEmpty(0).Max();

    [Fact]
    public void HugeAndPathologicalInput_IsParsedInLinearTime()
    {
        var inputs = new[]
        {
            new string('`', 400_000),
            string.Concat(Enumerable.Range(1, 900).Select(length => new string('`', length) + " ")),
            string.Concat(Enumerable.Repeat("` ", 300_000)),
            string.Concat(Enumerable.Repeat("**a ", 300_000)),
            string.Concat(Enumerable.Repeat("- x\n  ", 100_000)),
            string.Concat(Enumerable.Repeat("```\n", 100_000)),
            string.Join("\n", Enumerable.Range(0, 60_000).Select(i => new string(' ', i % 40) + "- item " + i)),
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            Assert.NotNull(SummaryMarkdown.Parse(input));
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"parsing took {stopwatch.Elapsed}");
    }
}
