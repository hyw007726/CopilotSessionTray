using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App;

/// <summary>Turns a session's summary into a read-only <see cref="FlowDocument"/> for the summary panel.</summary>
internal static class SummaryDocumentBuilder
{
    private static readonly FontFamily BodyFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas");
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0x5F, 0x5F, 0x5F));
    private static readonly Brush RuleBrush = Frozen(Color.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly Brush InlineCodeBackground = Frozen(Color.FromRgb(0xE9, 0xEB, 0xEE));
    private static readonly Brush CodeBlockBackground = Frozen(Color.FromRgb(0xF0, 0xF1, 0xF3));

    /// <param name="view">The reader-facing summary, or null for rows that only have placeholder text.</param>
    /// <param name="fallbackText">Markdown shown when <paramref name="view"/> is null.</param>
    public static FlowDocument Build(SessionSummaryView? view, string fallbackText)
    {
        var document = new FlowDocument
        {
            FontFamily = BodyFont,
            FontSize = 12.5,
            LineHeight = 18,
            PagePadding = new Thickness(2),
            TextAlignment = TextAlignment.Left,
        };

        if (view is null)
        {
            AddMarkdown(document.Blocks, fallbackText);
        }
        else if (view.Sections.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("There is nothing to summarize for this session yet.")) { Foreground = MutedBrush });
        }
        else
        {
            var first = true;
            foreach (var section in view.Sections)
            {
                document.Blocks.Add(SectionHeading(section.Heading, first));
                AddMarkdown(document.Blocks, section.Text);
                first = false;
            }
        }

        return document;
    }

    private static void AddMarkdown(BlockCollection blocks, string markdown)
    {
        foreach (var block in SummaryMarkdown.Parse(markdown))
        {
            blocks.Add(ToBlock(block));
        }
    }

    private static Block ToBlock(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => ParagraphOf(paragraph.Runs, new Thickness(0, 0, 0, 6)),
        MarkdownHeading heading => HeadingOf(heading),
        MarkdownList list => ListOf(list, nested: false),
        MarkdownCodeBlock code => CodeOf(code),
        _ => throw new InvalidOperationException($"Unsupported block {block.GetType().Name}"),
    };

    private static Paragraph SectionHeading(string text, bool first) => new(new Run(text.ToUpperInvariant()))
    {
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = MutedBrush,
        BorderBrush = RuleBrush,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(0, 0, 0, 2),
        Margin = new Thickness(0, first ? 0 : 12, 0, 6),
    };

    private static Paragraph HeadingOf(MarkdownHeading heading)
    {
        var paragraph = ParagraphOf(heading.Runs, new Thickness(0, 4, 0, 4));
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = heading.Level switch
        {
            1 => 16,
            2 => 14.5,
            _ => 13,
        };
        return paragraph;
    }

    private static Paragraph ParagraphOf(IReadOnlyList<MarkdownRun> runs, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddInlines(paragraph.Inlines, runs);
        return paragraph;
    }

    private static List ListOf(MarkdownList list, bool nested)
    {
        var result = new List
        {
            MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, list.Start),
            MarkerOffset = 6,
            Padding = new Thickness(20, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, nested ? 2 : 6),
        };

        foreach (var item in list.Items)
        {
            var listItem = new ListItem(ParagraphOf(item.Runs, new Thickness(0, 0, 0, 2)));
            foreach (var child in item.Children)
            {
                listItem.Blocks.Add(ListOf(child, nested: true));
            }

            result.ListItems.Add(listItem);
        }

        return result;
    }

    private static Paragraph CodeOf(MarkdownCodeBlock code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = CodeFont,
            FontSize = 11.5,
            LineHeight = 16,
            Background = CodeBlockBackground,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var lines = code.Text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[index]));
        }

        return paragraph;
    }

    private static void AddInlines(InlineCollection inlines, IReadOnlyList<MarkdownRun> runs)
    {
        foreach (var run in runs)
        {
            var lines = run.Text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    inlines.Add(new LineBreak());
                }

                if (lines[index].Length == 0)
                {
                    continue;
                }

                var element = new Run(lines[index]);
                if (run.Bold)
                {
                    element.FontWeight = FontWeights.Bold;
                }

                if (run.Italic)
                {
                    element.FontStyle = FontStyles.Italic;
                }

                if (run.Code)
                {
                    element.FontFamily = CodeFont;
                    element.FontSize = 12;
                    element.Background = InlineCodeBackground;
                }

                inlines.Add(element);
            }
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
