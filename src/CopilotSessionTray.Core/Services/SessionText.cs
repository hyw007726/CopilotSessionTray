using System.Text;
using System.Text.RegularExpressions;

namespace CopilotSessionTray.Core.Services;

/// <summary>Small, pure text helpers shared by the session title, notification and resume-summary code.</summary>
public static class SessionText
{
    private const string Ellipsis = "…";

    private static readonly Regex LeadingMarker = new(@"^(?:#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)+", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^#{1,6}\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Turns a markdown message into one line of plain text of at most <paramref name="maxLength"/>
    /// characters: fenced code is dropped, list/quote/heading markers, links, bold and code
    /// marks are stripped, and headings are only kept when there is nothing else.
    /// </summary>
    public static string ToPlainSnippet(string markdown, int maxLength)
    {
        var content = new List<string>();
        var headings = new List<string>();
        var inFence = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0)
            {
                continue;
            }

            var isHeading = Heading.IsMatch(line);
            line = LeadingMarker.Replace(line, string.Empty).Trim();
            if (line.Length > 0)
            {
                (isHeading ? headings : content).Add(line);
            }
        }

        var text = string.Join(' ', content.Count > 0 ? content : headings);
        text = Bold.Replace(Link.Replace(text, "$1"), "$1").Replace("`", string.Empty);
        return ToSingleLine(text, maxLength);
    }

    /// <summary>Formats a duration as its largest sensible units, e.g. <c>45s</c>, <c>12m</c>, <c>1h 5m</c> or <c>2d 3h</c>.</summary>
    public static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalSeconds: < 1 } => "0s",
        { TotalMinutes: < 1 } => $"{duration.Seconds}s",
        { TotalHours: < 1 } => $"{(int)duration.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)duration.TotalHours}h {duration.Minutes}m",
        _ => $"{(int)duration.TotalDays}d {duration.Hours}h",
    };

    /// <summary>
    /// Collapses every run of whitespace (including line breaks) to a single space, trims the
    /// result, and shortens it to at most <paramref name="maxLength"/> characters with a trailing
    /// ellipsis.
    /// </summary>
    public static string ToSingleLine(string text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length > maxLength)
            {
                break;
            }
        }

        var collapsed = builder.ToString();
        return collapsed.Length <= maxLength
            ? collapsed
            : TakePrefix(collapsed, maxLength - 1).TrimEnd() + Ellipsis;
    }

    internal static string TakePrefix(string text, int length)
    {
        if (length >= text.Length)
        {
            return text;
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}
