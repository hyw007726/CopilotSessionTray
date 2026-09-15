using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Reads the live <c>%USERPROFILE%\.copilot\open-sessions-state.json</c>
/// registry that Copilot CLI maintains for all currently-open sessions.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be strictly read-only: this file is owned and
/// actively rewritten by Copilot CLI processes, and must never be modified
/// by this application.
/// </para>
/// <para>
/// If the file does not exist (e.g. no Copilot session has ever run on this
/// machine), implementations return an empty result rather than throwing.
/// Transient I/O errors (e.g. the file is mid-write) should be retried with
/// a short backoff internally before surfacing an exception, since the file
/// is rewritten frequently by external processes.
/// </para>
/// <para>
/// This is a pull-based snapshot reader; callers (typically
/// <see cref="ISessionDetectionEngine"/>) are responsible for scheduling
/// repeated reads (timer and/or <see cref="System.IO.FileSystemWatcher"/>-driven).
/// </para>
/// </remarks>
public interface IOpenSessionsRegistryReader
{
    /// <summary>
    /// Reads the current snapshot of all open sessions known to Copilot CLI.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>
    /// A dictionary keyed by session id. Empty if the registry file does not
    /// exist or currently contains no sessions.
    /// </returns>
    Task<IReadOnlyDictionary<string, OpenSessionEntry>> ReadAsync(CancellationToken cancellationToken = default);
}
