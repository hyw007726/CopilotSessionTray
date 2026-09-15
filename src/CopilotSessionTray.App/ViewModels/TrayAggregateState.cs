namespace CopilotSessionTray.App.ViewModels;

/// <summary>
/// Aggregate, app-level tray icon state derived from all known sessions —
/// see IMPLEMENTATION_PLAN.md §3 ("App-level status"). This lives in the
/// App layer rather than <c>CopilotSessionTray.Core</c> because it is a UI
/// presentation concept (which icon/color to show), not a domain contract;
/// the per-session status vocabulary is <see cref="Core.Models.SessionStatus"/>.
/// </summary>
public enum TrayAggregateState
{
    /// <summary>No sessions are currently known.</summary>
    NoSessions,

    /// <summary>Sessions are open, but all are idle.</summary>
    Idle,

    /// <summary>At least one session is actively working.</summary>
    Working,

    /// <summary>At least one session finished and hasn't been acknowledged yet.</summary>
    AttentionNeeded,
}
