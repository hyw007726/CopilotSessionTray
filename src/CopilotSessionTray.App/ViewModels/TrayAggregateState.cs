namespace CopilotSessionTray.App.ViewModels;

/// <summary>
/// Aggregate, app-level tray icon state derived from all known sessions —
/// see IMPLEMENTATION_PLAN.md §3 ("App-level status") and §9.6. This lives in the
/// App layer rather than <c>CopilotSessionTray.Core</c> because it is a UI
/// presentation concept (which icon/color to show), not a domain contract;
/// the per-session status vocabulary is <see cref="Core.Models.SessionStatus"/>.
/// </summary>
/// <remarks>
/// <b>2026-10-05 (IMPLEMENTATION_PLAN.md §9.6):</b> this enum used to be computed every poll but
/// never actually read by anything — <c>TrayViewModel.IconBrush</c>/<c>IconGlyph</c>/
/// <c>WatermarkImageSource</c> each independently reimplemented their own
/// <c>IsAnyWorking</c>/<c>UnreadCount</c> checks. It's now the single, authoritative source of
/// truth those all switch over instead. Priority order (highest wins, computed in
/// <c>TrayViewModel.RecomputeAggregateState</c>): <see cref="AttentionNeeded"/> outranks
/// <see cref="Working"/> — "something finished and you haven't seen it" is more urgent than
/// "something's just running" — reversing the original priority, where <see cref="Working"/>
/// always won. <see cref="WaitingForInput"/> is a kept-but-unimplemented stub — see its own doc
/// comment.
/// </remarks>
public enum TrayAggregateState
{
    /// <summary>No sessions are currently known.</summary>
    NoSessions,

    /// <summary>Sessions are open, but all are idle (none working or unread).</summary>
    Idle,

    /// <summary>At least one session is actively working, and nothing higher-priority is also true.</summary>
    Working,

    /// <summary>At least one session finished and hasn't been acknowledged (read) yet.</summary>
    AttentionNeeded,

    /// <summary>
    /// <b>Interface kept, implementation intentionally removed (2026-10-05, IMPLEMENTATION_PLAN.md
    /// §9.6 follow-up) — never actually assigned by <c>TrayViewModel.RecomputeAggregateState</c>
    /// today.</b> Originally meant "at least one open session has sat idle since first seen,
    /// waiting on the user's next message", driving a dedicated gold tray-icon flash. Real
    /// evidence (a screenshot of genuinely-idle sessions, one 5+ hours old, one 1+ day old)
    /// exposed that Copilot CLI's own data has no way to distinguish "paused mid-conversation,
    /// expects a reply soon" from "fully done, abandoned a while ago" — both report
    /// <c>working: false</c> identically (IMPLEMENTATION_PLAN.md §2.3), so confidently flagging
    /// this as urgent was simply wrong. Left in the enum (rather than deleted) specifically so a
    /// future, real distinguishing signal from Copilot CLI (e.g. a genuine "awaiting reply" flag)
    /// can be wired back in at the same plug-in points (<c>TrayViewModel.IconBrush</c>/
    /// <c>WatermarkImageSource</c>'s switch arms) without redesigning where it belongs.
    /// </summary>
    WaitingForInput,
}
