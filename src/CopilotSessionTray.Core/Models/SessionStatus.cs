namespace CopilotSessionTray.Core.Models;

/// <summary>
/// High-level status of a single Copilot CLI session, derived by
/// <see cref="Contracts.ISessionDetectionEngine"/> from the raw signals in
/// <c>open-sessions-state.json</c>, the session's <c>events.jsonl</c>, and
/// OS process liveness.
/// </summary>
public enum SessionStatus
{
    /// <summary>The session is open and the agent is actively working (a turn/tool is in progress).</summary>
    Working,

    /// <summary>The session is open, idle, and waiting for user input; no unread completion.</summary>
    WaitingForInput,

    /// <summary>The session finished a turn/task while unattended and has not yet been acknowledged by the user.</summary>
    Finished,

    /// <summary>The owning process has exited (or a <c>session.shutdown</c> event was observed); no longer live.</summary>
    Closed,
}
