namespace CopilotSessionTray.Core.Models;

/// <summary>
/// The kind of state transition detected by <see cref="Contracts.ISessionDetectionEngine"/>
/// between two consecutive polls.
/// </summary>
public enum SessionChangeKind
{
    /// <summary>A session that wasn't previously known is now open.</summary>
    Started,

    /// <summary>The session's <see cref="OpenSessionEntry.Working"/> flag flipped.</summary>
    WorkingStateChanged,

    /// <summary>The session transitioned from working to idle/waiting-for-input (candidate for notification).</summary>
    Finished,

    /// <summary>The session's owning process is no longer alive, or a <c>session.shutdown</c> event was observed.</summary>
    Closed,
}
