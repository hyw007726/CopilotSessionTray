using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Builds the text that seeds a new Copilot CLI session from an earlier one ("Resume with
/// summary"): the session's latest checkpoint (overview and next steps), else a recap of its first
/// request and latest reply, else its title — behind a short header naming the original session.
/// </summary>
public static class ResumeSummaryBuilder
{
    internal const int MaxBodyLength = 4000;
    internal const int MaxOverviewLength = 1500;
    internal const int MaxNextStepsLength = 2300;
    internal const int MaxRecapAskLength = 1200;
    internal const int MaxRecapOutcomeLength = 2400;

    private const int MaxHeaderTitleLength = 80;
    private const string TruncationMarker = " … (truncated)";
    private const string NoSummaryMessage = "No summary is available for this session yet.";
    private const string HeaderStart = "[Context: this is a condensed summary of an earlier Copilot CLI session";
    private const string HeaderResumeHint = "it can be resumed directly with `/resume ";
    private const string HeaderInstruction = " Please acknowledge in one short line and wait for my next message.";

    private static readonly Regex ProvenanceHeader = new(
        $"^\\s*{Regex.Escape(HeaderStart)}.*?{Regex.Escape(HeaderResumeHint)}[^`]*`\\.[^\\]]*\\]\\s*",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex OriginId = new(
        $"^\\s*{Regex.Escape(HeaderStart)}.*?\\(id: ([^)\\s]+)\\)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Builds the seed text for <paramref name="sessionId"/> from whatever history could be read
    /// (<c>null</c> means that part was unavailable). <paramref name="title"/> is the session's
    /// title, used in the header and as the last-resort body.
    /// </summary>
    public static string Build(
        string sessionId, string? title, IReadOnlyList<SessionCheckpoint>? checkpoints, IReadOnlyList<SessionTurn>? turns) =>
        RenderSeed(sessionId, title, Compose(title, checkpoints, turns));

    /// <summary>
    /// Builds the reader-facing form of the same content <see cref="Build"/> sends to a new
    /// session — identical text and limits, without the header addressed to the model.
    /// </summary>
    public static SessionSummaryView BuildView(
        string? title, IReadOnlyList<SessionCheckpoint>? checkpoints, IReadOnlyList<SessionTurn>? turns) =>
        ToView(Compose(title, checkpoints, turns));

    internal sealed record SummaryPart(string Heading, string? SeedLabel, string Text);

    internal sealed record SummaryContent(
        SessionSummarySource Source, SessionCheckpoint? Checkpoint, IReadOnlyList<SummaryPart> Parts);

    internal static SummaryContent Compose(
        string? title, IReadOnlyList<SessionCheckpoint>? checkpoints, IReadOnlyList<SessionTurn>? turns)
    {
        var checkpoint = checkpoints?.LastOrDefault(candidate => IsPresent(candidate.Overview));
        if (checkpoint is not null)
        {
            return new SummaryContent(SessionSummarySource.Checkpoint, checkpoint, ComposeCheckpointParts(checkpoint));
        }

        var recap = turns is null ? null : ComposeRecapParts(turns);
        if (recap is not null)
        {
            return new SummaryContent(SessionSummarySource.Recap, null, recap);
        }

        return IsPresent(title)
            ? new SummaryContent(
                SessionSummarySource.TitleOnly, null, [new SummaryPart("Title", null, Truncate(title.Trim(), MaxBodyLength))])
            : new SummaryContent(SessionSummarySource.None, null, []);
    }

    internal static string RenderSeed(string sessionId, string? title, SummaryContent content)
    {
        var body = content.Parts.Count == 0
            ? NoSummaryMessage
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                content.Parts.Select(part => part.SeedLabel is null ? part.Text : $"{part.SeedLabel}: {part.Text}"));
        return WithHeader(sessionId, title, body);
    }

    internal static SessionSummaryView ToView(SummaryContent content) => new(
        content.Source,
        content.Parts.Select(part => new SessionSummarySection(part.Heading, part.Text)).ToList(),
        content.Checkpoint?.CheckpointNumber,
        content.Checkpoint?.CreatedAtUtc);

    /// <summary>
    /// Returns the id of the session this one was seeded from, if its first message is a header
    /// written by <see cref="Build"/>; otherwise null.
    /// </summary>
    public static string? FindOriginSessionId(IReadOnlyList<SessionTurn>? turns)
    {
        var first = turns?.Select(turn => turn.UserMessage).FirstOrDefault(IsPresent);
        if (first is null)
        {
            return null;
        }

        try
        {
            var match = OriginId.Match(first);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : SessionText.TakePrefix(text, maxLength) + TruncationMarker;

    internal static string? StripProvenanceHeader(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return ProvenanceHeader.Replace(text, string.Empty, 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static IReadOnlyList<SummaryPart>? ComposeRecapParts(IReadOnlyList<SessionTurn> turns)
    {
        var ask = turns
            .Select(turn => StripProvenanceHeader(turn.UserMessage))
            .FirstOrDefault(IsPresent);
        var outcome = turns
            .Select(turn => turn.AssistantResponse)
            .LastOrDefault(IsPresent);

        var parts = new List<SummaryPart>();
        if (ask is not null)
        {
            parts.Add(new SummaryPart("First request", "Originally asked", Truncate(ask.Trim(), MaxRecapAskLength)));
        }

        if (outcome is not null)
        {
            parts.Add(new SummaryPart("Latest reply", "Most recent outcome", Truncate(outcome.Trim(), MaxRecapOutcomeLength)));
        }

        return parts.Count > 0 ? parts : null;
    }

    private static IReadOnlyList<SummaryPart> ComposeCheckpointParts(SessionCheckpoint checkpoint)
    {
        var overview = checkpoint.Overview!.Trim();
        if (!IsPresent(checkpoint.NextSteps))
        {
            return [new SummaryPart("Overview", null, Truncate(overview, MaxBodyLength))];
        }

        return
        [
            new SummaryPart("Overview", null, Truncate(overview, MaxOverviewLength)),
            new SummaryPart("Next steps", "Next steps", Truncate(checkpoint.NextSteps.Trim(), MaxNextStepsLength)),
        ];
    }

    private static string WithHeader(string sessionId, string? title, string body)
    {
        var titleSuffix = IsPresent(title)
            ? $", titled \"{SessionText.ToSingleLine(title, MaxHeaderTitleLength)}\""
            : string.Empty;

        var header = $"{HeaderStart}{titleSuffix} " +
            $"(id: {sessionId}). It is not itself part of this request — if the full original conversation " +
            $"is ever needed, {HeaderResumeHint}{sessionId}`.{HeaderInstruction}]";
        return $"{header}{Environment.NewLine}{Environment.NewLine}{body}";
    }

    private static bool IsPresent([NotNullWhen(true)] string? text) => !string.IsNullOrWhiteSpace(text);
}
