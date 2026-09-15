using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// The core "brain" of the app: combines <see cref="IOpenSessionsRegistryReader"/>,
/// <see cref="ISessionEventStreamReader"/>, and <see cref="IProcessLivenessChecker"/>
/// to detect session state transitions across successive polls.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to be stateful: they retain the previous
/// poll's snapshot in memory so that <see cref="PollAsync"/> can diff
/// against it and report only what changed. Instances are intended to be
/// used as a single long-lived object driven by one scheduler (a timer
/// and/or <see cref="System.IO.FileSystemWatcher"/> callbacks) and are not
/// guaranteed to be safe to call concurrently from multiple threads —
/// callers must serialize calls to <see cref="PollAsync"/>.
/// </para>
/// <para>
/// This interface intentionally does not decide whether to notify the user;
/// it only reports what changed. Notification policy (muting, quiet hours,
/// watch-lists from <see cref="NotificationPreferences"/>) is applied by the
/// caller before invoking <see cref="INotificationService"/>.
/// </para>
/// </remarks>
public interface ISessionDetectionEngine
{
    /// <summary>
    /// Reads current state from the underlying sources, diffs it against the
    /// previous call's snapshot, and returns the set of transitions observed
    /// since then (empty if nothing changed).
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the poll.</param>
    Task<IReadOnlyList<SessionChangeEvent>> PollAsync(CancellationToken cancellationToken = default);
}
