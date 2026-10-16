using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Models;

/// <summary>Where a session's summary came from, ordered from least to most informative.</summary>
public enum SessionSummarySource
{
    /// <summary>Nothing is known about the session yet.</summary>
    None,

    /// <summary>Only the session's title is known.</summary>
    TitleOnly,

    /// <summary>There is no checkpoint, so the summary recaps the first request and the latest reply.</summary>
    Recap,

    /// <summary>The summary is the session's latest checkpoint.</summary>
    Checkpoint,
}

/// <summary>One labelled part of a <see cref="SessionSummaryView"/>.</summary>
/// <param name="Heading">What the part is, e.g. "Overview" or "Next steps".</param>
/// <param name="Text">The part's content, as markdown.</param>
public sealed record SessionSummarySection(string Heading, string Text);

/// <summary>
/// A session's summary as a person reads it: the same content that seeds a new session (see
/// <see cref="ResumeSummaryBuilder"/>), without the instructions addressed to the model.
/// </summary>
/// <param name="Source">Where the content came from.</param>
/// <param name="Sections">The content, in reading order; empty when <paramref name="Source"/> is <see cref="SessionSummarySource.None"/>.</param>
/// <param name="CheckpointNumber">The checkpoint's number, when <paramref name="Source"/> is <see cref="SessionSummarySource.Checkpoint"/>.</param>
/// <param name="WrittenAtUtc">When that checkpoint was recorded.</param>
public sealed record SessionSummaryView(
    SessionSummarySource Source,
    IReadOnlyList<SessionSummarySection> Sections,
    int? CheckpointNumber = null,
    DateTimeOffset? WrittenAtUtc = null)
{
    /// <summary>A one-line description of where the summary came from, e.g. "Checkpoint 2 · 38m ago".</summary>
    public string Describe(DateTimeOffset nowUtc) => Source switch
    {
        SessionSummarySource.Checkpoint => DescribeCheckpoint(nowUtc),
        SessionSummarySource.Recap => DescribeRecap(),
        SessionSummarySource.TitleOnly => "No checkpoint or conversation text yet · only the title is known",
        _ => "Nothing to summarize yet",
    };

    private string DescribeRecap()
    {
        var parts = Sections.Select(section => section.Heading.ToLowerInvariant()).ToList();
        return parts.Count == 0
            ? "No checkpoint yet"
            : $"No checkpoint yet · recap of the {string.Join(" and the ", parts)}";
    }

    private string DescribeCheckpoint(DateTimeOffset nowUtc)
    {
        var label = CheckpointNumber is { } number ? $"Checkpoint {number}" : "Latest checkpoint";
        if (WrittenAtUtc is not { } writtenAt)
        {
            return label;
        }

        var age = nowUtc - writtenAt;
        return age < TimeSpan.FromMinutes(1) ? $"{label} · just now" : $"{label} · {SessionText.FormatDuration(age)} ago";
    }
}
