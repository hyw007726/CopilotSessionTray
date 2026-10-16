using System.Text;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// A small, forgiving reader for the markdown in Copilot CLI summaries and replies: paragraphs,
/// headings, nested lists, fenced code, tables (kept as preformatted rows) and inline
/// <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c>. Anything it doesn't recognise stays as literal
/// text, so no input fails to parse or loses words, and parsing time is linear in the input size.
/// </summary>
public static class SummaryMarkdown
{
    /// <summary>The deepest list nesting kept; items nested any deeper join the deepest list.</summary>
    public const int MaxListDepth = 6;

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        return new BlockParser(markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')).Parse();
    }

    /// <summary>The text of <paramref name="blocks"/> without markers, list bullets or numbers, one block or item per line.</summary>
    public static string ToPlainText(IReadOnlyList<MarkdownBlock> blocks)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownParagraph paragraph:
                    lines.Add(Join(paragraph.Runs));
                    break;
                case MarkdownHeading heading:
                    lines.Add(Join(heading.Runs));
                    break;
                case MarkdownCodeBlock code:
                    lines.Add(code.Text);
                    break;
                case MarkdownList list:
                    AppendPlainText(list, lines);
                    break;
            }
        }

        return string.Join('\n', lines);
    }

    internal static IReadOnlyList<MarkdownRun> ParseInlines(string text)
    {
        var tokens = Tokenize(text);
        PairMarkers(tokens, Emphasis.Bold);
        PairMarkers(tokens, Emphasis.Italic);
        return BuildRuns(tokens);
    }

    private static string Join(IReadOnlyList<MarkdownRun> runs) => string.Concat(runs.Select(run => run.Text));

    private static void AppendPlainText(MarkdownList list, List<string> lines)
    {
        foreach (var item in list.Items)
        {
            lines.Add(Join(item.Runs));
            foreach (var child in item.Children)
            {
                AppendPlainText(child, lines);
            }
        }
    }

    private enum TokenKind
    {
        Text,
        Code,
        Marker,
    }

    private enum Emphasis
    {
        Bold,
        Italic,
    }

    private sealed class Token(TokenKind kind, string text, Emphasis emphasis = Emphasis.Bold, bool canOpen = false, bool canClose = false)
    {
        public TokenKind Kind { get; } = kind;

        public string Text { get; } = text;

        public Emphasis Emphasis { get; } = emphasis;

        public bool CanOpen { get; } = canOpen;

        public bool CanClose { get; } = canClose;

        public bool Paired { get; set; }
    }

    private static List<Token> Tokenize(string text)
    {
        var runs = FindBacktickRuns(text);
        var nextOfSameLength = LinkRunsOfEqualLength(runs);
        var tokens = new List<Token>();
        var segmentStart = 0;

        for (var index = 0; index < runs.Count;)
        {
            var closeIndex = nextOfSameLength[index];
            if (closeIndex < 0)
            {
                index++;
                continue;
            }

            var open = runs[index];
            var close = runs[closeIndex];
            AddTextTokens(text, segmentStart, open.Start, tokens);
            tokens.Add(new Token(TokenKind.Code, CodeSpanContent(text, open.Start + open.Length, close.Start)));
            segmentStart = close.Start + close.Length;
            index = closeIndex + 1;
        }

        AddTextTokens(text, segmentStart, text.Length, tokens);
        return tokens;
    }

    private static List<(int Start, int Length)> FindBacktickRuns(string text)
    {
        var runs = new List<(int Start, int Length)>();
        for (var position = 0; position < text.Length;)
        {
            if (text[position] != '`')
            {
                position++;
                continue;
            }

            var start = position;
            while (position < text.Length && text[position] == '`')
            {
                position++;
            }

            runs.Add((start, position - start));
        }

        return runs;
    }

    private static int[] LinkRunsOfEqualLength(List<(int Start, int Length)> runs)
    {
        var next = new int[runs.Count];
        var latestByLength = new Dictionary<int, int>();
        for (var index = runs.Count - 1; index >= 0; index--)
        {
            next[index] = latestByLength.TryGetValue(runs[index].Length, out var later) ? later : -1;
            latestByLength[runs[index].Length] = index;
        }

        return next;
    }

    private static string CodeSpanContent(string text, int from, int to)
    {
        var content = text[from..to];
        return content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0
            ? content[1..^1]
            : content;
    }

    private static void AddTextTokens(string text, int from, int to, List<Token> tokens)
    {
        var textStart = from;
        var position = from;
        while (position < to)
        {
            if (text[position] != '*')
            {
                position++;
                continue;
            }

            var runEnd = position;
            while (runEnd < to && text[runEnd] == '*')
            {
                runEnd++;
            }

            var length = runEnd - position;
            if (length > 2)
            {
                position = runEnd;
                continue;
            }

            if (position > textStart)
            {
                tokens.Add(new Token(TokenKind.Text, text[textStart..position]));
            }

            tokens.Add(length == 2 ? BoldMarker(text, position, runEnd) : ItalicMarker(text, position, runEnd));
            position = textStart = runEnd;
        }

        if (textStart < to)
        {
            tokens.Add(new Token(TokenKind.Text, text[textStart..to]));
        }
    }

    private static Token BoldMarker(string text, int start, int end) => new(
        TokenKind.Marker,
        "**",
        Emphasis.Bold,
        canOpen: end < text.Length && !char.IsWhiteSpace(text[end]),
        canClose: start > 0 && !char.IsWhiteSpace(text[start - 1]));

    private static Token ItalicMarker(string text, int start, int end) => new(
        TokenKind.Marker,
        "*",
        Emphasis.Italic,
        canOpen: end < text.Length && !char.IsWhiteSpace(text[end]) && (start == 0 || !IsWordCharacter(text[start - 1])),
        canClose: start > 0 && !char.IsWhiteSpace(text[start - 1]) && (end == text.Length || !IsWordCharacter(text[end])));

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static void PairMarkers(List<Token> tokens, Emphasis emphasis)
    {
        Token? open = null;
        foreach (var token in tokens)
        {
            if (token.Kind != TokenKind.Marker || token.Emphasis != emphasis)
            {
                continue;
            }

            if (open is not null && token.CanClose)
            {
                open.Paired = true;
                token.Paired = true;
                open = null;
            }
            else if (open is null && token.CanOpen)
            {
                open = token;
            }
        }
    }

    private static List<MarkdownRun> BuildRuns(List<Token> tokens)
    {
        var runs = new List<MarkdownRun>();
        var buffer = new StringBuilder();
        var bufferBold = false;
        var bufferItalic = false;
        var bold = false;
        var italic = false;

        void FlushBuffer()
        {
            if (buffer.Length > 0)
            {
                runs.Add(new MarkdownRun(buffer.ToString(), bufferBold, Italic: bufferItalic));
                buffer.Clear();
            }
        }

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Marker when token.Paired:
                    FlushBuffer();
                    if (token.Emphasis == Emphasis.Bold)
                    {
                        bold = !bold;
                    }
                    else
                    {
                        italic = !italic;
                    }

                    break;
                case TokenKind.Code:
                    FlushBuffer();
                    if (token.Text.Length > 0)
                    {
                        runs.Add(new MarkdownRun(token.Text, bold, Code: true, Italic: italic));
                    }

                    break;
                default:
                    if (buffer.Length > 0 && (bufferBold != bold || bufferItalic != italic))
                    {
                        FlushBuffer();
                    }

                    bufferBold = bold;
                    bufferItalic = italic;
                    buffer.Append(token.Text);
                    break;
            }
        }

        FlushBuffer();
        return runs;
    }

    private sealed class PendingItem(int indent, bool ordered, int number, string text)
    {
        private readonly StringBuilder _text = new(text);

        public int Indent { get; } = indent;

        public bool Ordered { get; } = ordered;

        public int Number { get; } = number;

        public bool IsEmpty => _text.Length == 0;

        public string Text => _text.ToString();

        public void Append(string continuation)
        {
            if (_text.Length > 0)
            {
                _text.Append(' ');
            }

            _text.Append(continuation);
        }
    }

    private sealed class ListFrame(int indent, bool ordered, int start)
    {
        public int Indent { get; } = indent;

        public bool Ordered { get; } = ordered;

        public int Start { get; } = start;

        public List<ItemFrame> Items { get; } = [];

        public MarkdownList ToBlock() => new(Ordered, Start, Items.Select(item => item.ToItem()).ToList());
    }

    private sealed class ItemFrame(IReadOnlyList<MarkdownRun> runs)
    {
        public List<ListFrame> Children { get; } = [];

        public MarkdownListItem ToItem() => new(runs, Children.Select(child => child.ToBlock()).ToList());
    }

    private sealed class BlockParser(string[] lines)
    {
        private readonly List<MarkdownBlock> _blocks = [];
        private readonly List<string> _paragraph = [];
        private readonly List<PendingItem> _items = [];
        private bool _blankSinceItem;

        public IReadOnlyList<MarkdownBlock> Parse()
        {
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var trimmed = line.Trim();

                if (TryReadFence(trimmed, out var fenceCharacter, out var fenceLength))
                {
                    FlushParagraph();
                    FlushList();
                    index = ReadFencedCode(index, CountLeadingSpaces(line), fenceCharacter, fenceLength);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushParagraph();
                    _blankSinceItem = _items.Count > 0;
                    continue;
                }

                if (IsTableLine(trimmed))
                {
                    FlushParagraph();
                    FlushList();
                    index = ReadTable(index);
                    continue;
                }

                if (TryReadHeading(trimmed, out var level, out var headingText))
                {
                    FlushParagraph();
                    FlushList();
                    if (headingText.Length > 0)
                    {
                        _blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                    }

                    continue;
                }

                if (IsThematicBreak(trimmed))
                {
                    FlushParagraph();
                    FlushList();
                    continue;
                }

                if (TryReadListMarker(line, out var indent, out var ordered, out var number, out var itemText))
                {
                    FlushParagraph();
                    _items.Add(new PendingItem(indent, ordered, number, itemText));
                    _blankSinceItem = false;
                    continue;
                }

                if (_items.Count > 0 && !_blankSinceItem && char.IsWhiteSpace(line[0]))
                {
                    _items[^1].Append(trimmed);
                    continue;
                }

                FlushList();
                _paragraph.Add(StripQuoteMarker(trimmed));
            }

            FlushParagraph();
            FlushList();
            return _blocks;
        }

        private void FlushParagraph()
        {
            if (_paragraph.Count == 0)
            {
                return;
            }

            _blocks.Add(new MarkdownParagraph(ParseInlines(string.Join('\n', _paragraph))));
            _paragraph.Clear();
        }

        private void FlushList()
        {
            if (_items.Count == 0)
            {
                return;
            }

            var roots = new List<ListFrame>();
            var stack = new List<ListFrame>();
            foreach (var item in _items)
            {
                if (item.IsEmpty)
                {
                    continue;
                }

                while (stack.Count > 0 && item.Indent < stack[^1].Indent)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (stack.Count > 0 && item.Indent == stack[^1].Indent && item.Ordered != stack[^1].Ordered)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (stack.Count == 0 || (item.Indent > stack[^1].Indent && stack.Count < MaxListDepth))
                {
                    var frame = new ListFrame(item.Indent, item.Ordered, item.Number);
                    if (stack.Count > 0)
                    {
                        stack[^1].Items[^1].Children.Add(frame);
                    }
                    else
                    {
                        roots.Add(frame);
                    }

                    stack.Add(frame);
                }

                stack[^1].Items.Add(new ItemFrame(ParseInlines(item.Text)));
            }

            foreach (var root in roots)
            {
                _blocks.Add(root.ToBlock());
            }

            _items.Clear();
            _blankSinceItem = false;
        }

        private int ReadFencedCode(int openingIndex, int fenceIndent, char fenceCharacter, int fenceLength)
        {
            var content = new List<string>();
            var index = openingIndex + 1;
            for (; index < lines.Length; index++)
            {
                if (IsClosingFence(lines[index].Trim(), fenceCharacter, fenceLength))
                {
                    break;
                }

                content.Add(RemoveIndent(lines[index], fenceIndent));
            }

            while (content.Count > 0 && content[^1].Trim().Length == 0)
            {
                content.RemoveAt(content.Count - 1);
            }

            if (content.Count > 0)
            {
                _blocks.Add(new MarkdownCodeBlock(string.Join('\n', content)));
            }

            return Math.Min(index, lines.Length - 1);
        }

        private int ReadTable(int firstIndex)
        {
            var rows = new List<string>();
            var index = firstIndex;
            for (; index < lines.Length; index++)
            {
                var candidate = lines[index].Trim();
                if (!IsTableLine(candidate))
                {
                    break;
                }

                if (!IsTableSeparator(candidate))
                {
                    rows.Add(candidate);
                }
            }

            if (rows.Count > 0)
            {
                _blocks.Add(new MarkdownCodeBlock(string.Join('\n', rows)));
            }

            return index - 1;
        }
    }

    private static bool TryReadFence(string trimmed, out char character, out int length)
    {
        character = '\0';
        length = 0;
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return false;
        }

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == trimmed[0])
        {
            run++;
        }

        if (run < 3 || (trimmed[0] == '`' && trimmed.IndexOf('`', run) >= 0))
        {
            return false;
        }

        character = trimmed[0];
        length = run;
        return true;
    }

    private static bool IsClosingFence(string trimmed, char character, int length)
    {
        if (trimmed.Length < length)
        {
            return false;
        }

        foreach (var candidate in trimmed)
        {
            if (candidate != character)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string RemoveIndent(string line, int maximum) => line[Math.Min(maximum, CountLeadingSpaces(line))..];

    private static bool IsTableLine(string trimmed) => trimmed.Length >= 2 && trimmed[0] == '|' && trimmed[^1] == '|';

    private static bool IsTableSeparator(string trimmed)
    {
        var hasDash = false;
        foreach (var character in trimmed)
        {
            if (character == '-')
            {
                hasDash = true;
            }
            else if (character is not ('|' or ':' or ' '))
            {
                return false;
            }
        }

        return hasDash;
    }

    private static bool TryReadHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is < 1 or > 6 || hashes >= trimmed.Length || !char.IsWhiteSpace(trimmed[hashes]))
        {
            return false;
        }

        text = trimmed[hashes..].Trim();
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#')
        {
            end--;
        }

        if (end == 0 || char.IsWhiteSpace(text[end - 1]))
        {
            text = text[..end].TrimEnd();
        }

        level = hashes;
        return true;
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] is not ('-' or '*' or '_'))
        {
            return false;
        }

        var count = 0;
        foreach (var character in trimmed)
        {
            if (character == trimmed[0])
            {
                count++;
            }
            else if (character != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static bool TryReadListMarker(string line, out int indent, out bool ordered, out int number, out string text)
    {
        indent = 0;
        ordered = false;
        number = 0;
        text = string.Empty;

        var position = 0;
        while (position < line.Length && line[position] is ' ' or '\t')
        {
            indent += line[position] == '\t' ? 4 : 1;
            position++;
        }

        if (position >= line.Length)
        {
            return false;
        }

        var marker = line[position];
        if (marker is '-' or '*' or '+' or '•')
        {
            if (position + 1 == line.Length || char.IsWhiteSpace(line[position + 1]))
            {
                text = line[Math.Min(position + 1, line.Length)..].Trim();
                return true;
            }

            return false;
        }

        var digits = 0;
        while (position + digits < line.Length && char.IsAsciiDigit(line[position + digits]))
        {
            digits++;
        }

        if (digits is < 1 or > 3 || position + digits >= line.Length || line[position + digits] is not ('.' or ')'))
        {
            return false;
        }

        var after = position + digits + 1;
        if (after < line.Length && !char.IsWhiteSpace(line[after]))
        {
            return false;
        }

        ordered = true;
        number = int.Parse(line.AsSpan(position, digits));
        text = line[Math.Min(after, line.Length)..].Trim();
        return true;
    }

    private static string StripQuoteMarker(string trimmed)
    {
        var position = 0;
        while (position < trimmed.Length && trimmed[position] == '>')
        {
            position++;
        }

        if (position == 0 || (position < trimmed.Length && trimmed[position] != ' '))
        {
            return trimmed;
        }

        return trimmed[position..].TrimStart();
    }
}
