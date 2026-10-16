using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Read-only access to the <c>name</c>, <c>user_named</c>, <c>cwd</c> and <c>repository</c> fields
/// of a session's <c>workspace.yaml</c>. Deliberately a small, defensive reader rather than a YAML
/// library: it understands the scalar forms Copilot CLI actually writes (plain, single- and
/// double-quoted, and block scalars) and reports nothing for anything else, so a format change
/// degrades to "no name" instead of an error.
/// </summary>
public sealed class SessionWorkspaceReader
{
    private const int MaxCharacters = 262_144;

    private static readonly string DefaultSessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private static readonly Regex TopLevelKey = new(
        @"^([A-Za-z_][A-Za-z0-9_-]*):(?:[ \t]+(.*))?$", RegexOptions.CultureInvariant);

    private readonly string _sessionStateRoot;

    /// <summary>
    /// Creates a reader against the real <c>session-state</c> folder. An explicit
    /// <paramref name="sessionStateRoot"/> override exists solely so tests can point this at a
    /// temp folder.
    /// </summary>
    public SessionWorkspaceReader(string? sessionStateRoot = null)
    {
        _sessionStateRoot = sessionStateRoot ?? DefaultSessionStateRoot;
    }

    /// <summary>Reads the session's <c>workspace.yaml</c>, or returns null if it is missing or unreadable.</summary>
    public SessionWorkspaceInfo? Read(string sessionId)
    {
        if (sessionId is "." or ".." || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                Path.Combine(_sessionStateRoot, sessionId, "workspace.yaml"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxCharacters];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return Parse(new string(buffer, 0, count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static SessionWorkspaceInfo Parse(string text)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        string? name = null;
        string? cwd = null;
        string? repository = null;
        var userNamed = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var match = TopLevelKey.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value;
            if (key is not ("name" or "user_named" or "cwd" or "repository"))
            {
                continue;
            }

            var (value, lastLine) = ReadScalar(lines, index, match.Groups[2].Value);
            index = lastLine;
            switch (key)
            {
                case "name":
                    name = NullIfBlank(value);
                    break;
                case "cwd":
                    cwd = NullIfBlank(value);
                    break;
                case "repository":
                    repository = NullIfBlank(value);
                    break;
                default:
                    userNamed = string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new SessionWorkspaceInfo(name, userNamed, cwd, repository);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string? Value, int LastLine) ReadScalar(string[] lines, int index, string rest)
    {
        var first = rest.Trim();
        if (first.Length == 0)
        {
            return (null, index);
        }

        return first[0] switch
        {
            '|' or '>' => ReadBlockScalar(lines, index),
            '\'' or '"' => ReadQuotedScalar(lines, index, first),
            _ => ReadPlainScalar(lines, index, first),
        };
    }

    private static (string? Value, int LastLine) ReadBlockScalar(string[] lines, int index)
    {
        var content = new List<string>();
        var last = index;
        for (var next = index + 1; next < lines.Length; next++)
        {
            var line = lines[next];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            content.Add(line);
            last = next;
        }

        var indent = content
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        var text = string.Join('\n', content.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
        return (text.TrimEnd(), last);
    }

    private static (string? Value, int LastLine) ReadQuotedScalar(string[] lines, int index, string first)
    {
        var quote = first[0];
        var builder = new StringBuilder();
        var current = index;
        var line = first[1..];

        while (true)
        {
            var joinWithoutSpace = false;
            for (var position = 0; position < line.Length;)
            {
                var character = line[position];
                if (quote == '\'')
                {
                    if (character == '\'')
                    {
                        if (position + 1 < line.Length && line[position + 1] == '\'')
                        {
                            builder.Append('\'');
                            position += 2;
                            continue;
                        }

                        return (builder.ToString(), current);
                    }

                    builder.Append(character);
                    position++;
                }
                else
                {
                    if (character == '"')
                    {
                        return (builder.ToString(), current);
                    }

                    if (character == '\\')
                    {
                        if (position + 1 >= line.Length)
                        {
                            joinWithoutSpace = true;
                            position++;
                            continue;
                        }

                        position += Unescape(line, position, builder);
                        continue;
                    }

                    builder.Append(character);
                    position++;
                }
            }

            current++;
            if (current >= lines.Length)
            {
                return (null, lines.Length - 1);
            }

            if (!joinWithoutSpace)
            {
                var trimmed = builder.ToString().TrimEnd(' ', '\t');
                builder.Clear().Append(trimmed);
            }

            var blankLines = 0;
            while (current < lines.Length && lines[current].Trim().Length == 0)
            {
                blankLines++;
                current++;
            }

            if (current >= lines.Length)
            {
                return (null, lines.Length - 1);
            }

            if (blankLines > 0)
            {
                builder.Append('\n', blankLines);
            }
            else if (!joinWithoutSpace)
            {
                builder.Append(' ');
            }

            line = lines[current].TrimStart();
        }
    }

    private static int Unescape(string line, int position, StringBuilder builder)
    {
        var escape = line[position + 1];
        switch (escape)
        {
            case '0': builder.Append('\0'); return 2;
            case 'a': builder.Append('\a'); return 2;
            case 'b': builder.Append('\b'); return 2;
            case 't' or '\t': builder.Append('\t'); return 2;
            case 'n': builder.Append('\n'); return 2;
            case 'v': builder.Append('\v'); return 2;
            case 'f': builder.Append('\f'); return 2;
            case 'r': builder.Append('\r'); return 2;
            case 'e': builder.Append('\u001b'); return 2;
            case ' ': builder.Append(' '); return 2;
            case '_': builder.Append('\u00a0'); return 2;
            case 'N': builder.Append('\u0085'); return 2;
            case 'L': builder.Append('\u2028'); return 2;
            case 'P': builder.Append('\u2029'); return 2;
            case 'x': return AppendCodePoint(line, position, 2, builder);
            case 'u': return AppendCodePoint(line, position, 4, builder);
            case 'U': return AppendCodePoint(line, position, 8, builder);
            default: builder.Append(escape); return 2;
        }
    }

    private static int AppendCodePoint(string line, int position, int digits, StringBuilder builder)
    {
        var start = position + 2;
        if (start + digits <= line.Length &&
            int.TryParse(line.AsSpan(start, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint) &&
            codePoint is >= 0 and <= 0x10FFFF && !(codePoint is >= 0xD800 and <= 0xDFFF))
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
            return 2 + digits;
        }

        builder.Append(line[position + 1]);
        return 2;
    }

    private static (string? Value, int LastLine) ReadPlainScalar(string[] lines, int index, string first)
    {
        var value = StripComment(first);
        var last = index;
        for (var next = index + 1; next < lines.Length; next++)
        {
            var line = lines[next];
            if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var piece = StripComment(line.Trim());
            if (piece.Length == 0)
            {
                break;
            }

            value += " " + piece;
            last = next;
        }

        return value is "~" or "null" or "Null" or "NULL" ? (null, last) : (value, last);
    }

    private static string StripComment(string text)
    {
        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? text[..comment] : text).TrimEnd();
    }
}
