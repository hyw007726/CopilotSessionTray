using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>Picks the one short piece of text a sticker shows under a session's title.</summary>
public static class SessionGist
{
    public const int MaxLength = 240;

    /// <summary>Flattens <paramref name="text"/> to a single plain line of at most <see cref="MaxLength"/> characters, or null when nothing readable is left.</summary>
    public static string? Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var snippet = SessionText.ToPlainSnippet(text, MaxLength);
        return snippet.Length == 0 ? null : snippet;
    }

    /// <summary>
    /// What the session is up to: the request it is working on while it works, otherwise what its
    /// last turn reported; each falls back to the other when it is unknown.
    /// </summary>
    public static string? Choose(SessionStatus status, string? request, string? outcome) =>
        status == SessionStatus.Working
            ? Snippet(request) ?? Snippet(outcome)
            : Snippet(outcome) ?? Snippet(request);

    /// <summary>The most recent reply of a recap, or the overview of a checkpoint; null when the view holds neither.</summary>
    public static string? FromView(SessionSummaryView? view)
    {
        if (view is null || view.Sections.Count == 0)
        {
            return null;
        }

        return view.Source switch
        {
            SessionSummarySource.Recap => Snippet(view.Sections[^1].Text),
            SessionSummarySource.Checkpoint => Snippet(view.Sections[0].Text),
            _ => null,
        };
    }
}
