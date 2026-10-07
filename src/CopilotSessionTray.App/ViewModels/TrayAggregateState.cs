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
/// never actually read by anything — <c>TrayViewModel</c>'s tray-icon/watermark rendering each
/// independently reimplemented their own <c>IsAnyWorking</c> checks. It's now the single,
/// authoritative source of truth those switch over instead (the actual Win32 tray icon was
/// redesigned 2026-10-06 to a composited glyph+ring — see <c>MainWindow.xaml.cs</c>'s
/// <c>ComposeTrayIconBitmap</c> — driven directly off <c>TrayViewModel.IsAnyWorking</c> rather
/// than this enum, but <c>TrayViewModel.WatermarkImageSource</c> — the popup's background art —
/// still switches over this). <see cref="WaitingForInput"/> is a kept-but-unimplemented stub —
/// see its own doc comment.
/// </remarks>
public enum TrayAggregateState
{
    /// <summary>No sessions are currently known.</summary>
    NoSessions,

    /// <summary>Sessions are open, but none are working.</summary>
    Idle,

    /// <summary>At least one session is actively working.</summary>
    Working,

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
    /// can be wired back in at the same plug-in point (<c>TrayViewModel.WatermarkImageSource</c>'s
    /// switch arms) without redesigning where it belongs.
    /// </summary>
    WaitingForInput,
}
