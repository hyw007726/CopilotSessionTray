using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Read-only access to Copilot CLI's durable history in
/// <c>%USERPROFILE%\.copilot\session-store.db</c> (SQLite, WAL mode) — used
/// for session metadata, history browsing, and search; not for real-time
/// "is it running" state (use <see cref="IOpenSessionsRegistryReader"/> and
/// <see cref="ISessionEventStreamReader"/> for that).
/// </summary>
/// <remarks>
/// <para>
/// Implementations must open the database strictly read-only (e.g. a
/// <c>Mode=ReadOnly</c> connection string) and must never write to it — it
/// is actively written by live Copilot CLI processes.
/// </para>
/// <para>
/// Because the database can be briefly locked by a concurrent writer,
/// implementations should retry with backoff on a busy/locked error before
/// surfacing an exception.
/// </para>
/// </remarks>
public interface ISessionHistoryStore
{
    /// <summary>Gets the most recently updated sessions, newest first.</summary>
    /// <param name="maxCount">Maximum number of sessions to return.</param>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Gets a single session's summary by id, or null if not found.</summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Gets all turns recorded for a session, in turn order.</summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<IReadOnlyList<SessionTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Gets all checkpoints recorded for a session, in checkpoint order.</summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<IReadOnlyList<SessionCheckpoint>> GetCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default);
}
