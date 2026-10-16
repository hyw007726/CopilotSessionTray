namespace CopilotSessionTray.Core.Models;

/// <summary>A stretch of inline text. Line breaks inside <paramref name="Text"/> are kept as <c>\n</c>.</summary>
public readonly record struct MarkdownRun(string Text, bool Bold = false, bool Code = false, bool Italic = false);

/// <summary>A block of parsed markdown, as produced by <see cref="Services.SummaryMarkdown"/>.</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>A bulleted or numbered list; <paramref name="Start"/> is the first number of a numbered one.</summary>
public sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>Preformatted text: fenced code, or a table shown as monospace rows.</summary>
public sealed record MarkdownCodeBlock(string Text) : MarkdownBlock;

public sealed record MarkdownListItem(IReadOnlyList<MarkdownRun> Runs, IReadOnlyList<MarkdownList> Children);
