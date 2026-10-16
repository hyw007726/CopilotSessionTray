using System.Diagnostics.CodeAnalysis;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>The title, resume text, origin and reader-facing view of one session, as resolved by <see cref="SessionSummaryProvider"/>.</summary>
/// <param name="Title">The session's best available title, or null if none could be found.</param>
/// <param name="ResumeText">The text that seeds a new session from this one (see <see cref="ResumeSummaryBuilder"/>).</param>
/// <param name="OriginSessionId">The session this one was seeded from by "Resume with summary", if any.</param>
/// <param name="View">The same content as <paramref name="ResumeText"/>, laid out for a person to read.</param>
public sealed record SessionSummaryDetails(string? Title, string ResumeText, string? OriginSessionId, SessionSummaryView View);

/// <summary>
/// Single owner of "what is this session called and what is it about": combines the history
/// database with the session's <c>workspace.yaml</c>. Every read is best-effort — a failed or
/// missing source simply drops out of the result.
/// </summary>
public sealed class SessionSummaryProvider
{
    private readonly ISessionHistoryStore _historyStore;
    private readonly SessionWorkspaceReader _workspaceReader;

    public SessionSummaryProvider(ISessionHistoryStore historyStore, SessionWorkspaceReader workspaceReader)
    {
        _historyStore = historyStore;
        _workspaceReader = workspaceReader;
    }

    /// <summary>Resolves the title, resume text and origin for <paramref name="sessionId"/>.</summary>
    /// <param name="summary">The session's database row, if already known.</param>
    public async Task<SessionSummaryDetails> GetAsync(
        string sessionId, SessionSummary? summary, CancellationToken cancellationToken = default)
    {
        var checkpoints = await TryReadAsync(() => _historyStore.GetCheckpointsAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        var turns = await TryReadAsync(() => _historyStore.GetTurnsAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        var title = ResolveTitle(summary, _workspaceReader.Read(sessionId), checkpoints);
        var content = ResumeSummaryBuilder.Compose(title, checkpoints, turns);

        return new SessionSummaryDetails(
            title,
            ResumeSummaryBuilder.RenderSeed(sessionId, title, content),
            ResumeSummaryBuilder.FindOriginSessionId(turns),
            ResumeSummaryBuilder.ToView(content));
    }

    /// <summary>
    /// Resolves just the title for <paramref name="sessionId"/> — cheaper than <see cref="GetAsync"/>
    /// since it never reads the session's turns.
    /// </summary>
    /// <param name="summary">The session's database row; read from the database when null.</param>
    public async Task<string?> GetTitleAsync(
        string sessionId, SessionSummary? summary = null, CancellationToken cancellationToken = default)
    {
        summary ??= await TryReadAsync(() => _historyStore.GetSessionAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        var checkpoints = await TryReadAsync(() => _historyStore.GetCheckpointsAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        return ResolveTitle(summary, _workspaceReader.Read(sessionId), checkpoints);
    }

    internal static string? ResolveTitle(
        SessionSummary? summary, SessionWorkspaceInfo? workspace, IReadOnlyList<SessionCheckpoint>? checkpoints)
    {
        if (workspace is { UserNamed: true } && IsPresent(workspace.Name))
        {
            return workspace.Name;
        }

        var checkpointTitle = checkpoints?.Select(checkpoint => checkpoint.Title).LastOrDefault(IsPresent);
        if (checkpointTitle is not null)
        {
            return checkpointTitle;
        }

        if (IsPresent(summary?.Summary))
        {
            return summary.Summary;
        }

        return IsPresent(workspace?.Name) && !workspace.Name.Contains('\n') ? workspace.Name : null;
    }

    private static async Task<T?> TryReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    private static bool IsPresent([NotNullWhen(true)] string? text) => !string.IsNullOrWhiteSpace(text);
}
