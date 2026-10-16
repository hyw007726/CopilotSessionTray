using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.App.ViewModels;

/// <summary>
/// A single row in the tray popup's session list.
///
/// Phase 1 note: instances are populated from static fake data in
/// <see cref="TrayViewModel"/>, not from any real Copilot CLI state — see
/// IMPLEMENTATION_PLAN.md §10 Phase 1 ("fake/static data only"). The real
/// data will come from <c>CopilotSessionTray.Core</c>'s reviewed contracts
/// once Phase 2 lands.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(
        string id,
        string displayName,
        SessionStatus status,
        string detail,
        TimeSpan elapsed,
        string? workingDirectory = null,
        string? realSummary = null,
        SessionSummaryView? summaryView = null)
    {
        Id = id;
        _displayName = displayName;
        _status = status;
        _detail = detail;
        _elapsed = elapsed;
        _stateSinceUtc = DateTimeOffset.UtcNow - elapsed;
        WorkingDirectory = workingDirectory;
        _summary = !string.IsNullOrWhiteSpace(realSummary)
            ? realSummary
            : $"Summary of prior work on {displayName}: {detail} (last status: {StatusLabelFor(status)}).";
        _summaryView = summaryView;
    }

    public string Id { get; }

    /// <summary>
    /// The directory this session was started from, if known — mirrors
    /// <see cref="Core.Models.SessionSummary.Cwd"/>, used by "resume in
    /// terminal" so the new pane opens in the right place.
    /// </summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// For real sessions: the text built by <see cref="Core.Services.ResumeSummaryBuilder"/> —
    /// the latest checkpoint (overview and next steps), else a recap of the first request and
    /// latest reply, else the session title, behind a short header naming the original session.
    /// This is the text a new session is seeded with, not what the summary panel shows (see
    /// <see cref="SummaryView"/>). For Phase 1 demo rows only: a fabricated placeholder sentence.
    /// Independent of any later local rename via <see cref="DisplayName"/>.
    /// </summary>
    [ObservableProperty]
    private string _summary;

    /// <summary>
    /// The same content as <see cref="Summary"/>, laid out for a person to read in the summary
    /// panel. Null for Phase 1 demo rows, which only have the placeholder sentence.
    /// </summary>
    [ObservableProperty]
    private SessionSummaryView? _summaryView;

    internal void UpdateSummary(string summary, SessionSummaryView summaryView)
    {
        Summary = summary;
        SummaryView = summaryView;
    }

    /// <summary>
    /// This app's own local label for the session — renameable by the user
    /// (see the summary panel). Purely a local override kept via
    /// <c>IAppStateStore</c>; never renames the real Copilot CLI session
    /// (which has its own separate <c>/rename</c> concept this app never
    /// touches, per Core's read-only-against-<c>.copilot</c> rule).
    /// </summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>
    /// The session's current status — mutable (not just constructor-set) since 2026-09-29: the
    /// continuous background poll (<c>TrayViewModel</c>'s <c>ISessionDetectionEngine</c>-driven
    /// monitor) patches this in place on a real <c>Working</c>/<c>Finished</c>/<c>Closed</c>
    /// transition, rather than removing and re-adding the whole row.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusDotVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusIconVisibility))]
    private SessionStatus _status;

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedLabel))]
    private TimeSpan _elapsed;

    private DateTimeOffset _stateSinceUtc;

    /// <summary>Recomputes <see cref="Elapsed"/> as the time since the current state began.</summary>
    internal void RefreshElapsed(DateTimeOffset now)
    {
        Elapsed = now > _stateSinceUtc ? now - _stateSinceUtc : TimeSpan.Zero;
    }

    partial void OnStatusChanged(SessionStatus value)
    {
        _stateSinceUtc = DateTimeOffset.UtcNow;
        Elapsed = TimeSpan.Zero;
    }

    public string StatusLabel => StatusLabelFor(Status);

    /// <summary>
    /// Human-readable label for <see cref="Status"/>. <see cref="SessionStatus.WaitingForInput"/>
    /// displays as "Idle" (2026-10-05, IMPLEMENTATION_PLAN.md §9.6 follow-up — revised from an
    /// even earlier same-day "Completed" attempt): real session evidence (sessions idle for 5+
    /// hours / 1+ day) showed Copilot CLI's own <c>working</c> flag (IMPLEMENTATION_PLAN.md §2.3)
    /// can't distinguish "mid-conversation, paused for a quick reply" from "fully done, sitting
    /// idle" — both report <c>working: false</c> identically. "Completed" still overclaimed
    /// something this app can't actually know; "Idle" makes no claim either way, which is the
    /// honest answer until Copilot CLI exposes a real distinguishing signal (see
    /// <c>TrayAggregateState.WaitingForInput</c>'s own doc comment for the fuller reasoning — the
    /// underlying enum value and this app's "interface" for a future real implementation are
    /// deliberately kept, just not wired to anything confident today).
    /// </summary>
    private static string StatusLabelFor(SessionStatus status) => status switch
    {
        SessionStatus.Working => "Working",
        SessionStatus.WaitingForInput => "Idle",
        SessionStatus.Finished => "Finished",
        SessionStatus.Closed => "Closed",
        _ => "Unknown",
    };

    public string ElapsedLabel => Elapsed switch
    {
        { TotalMinutes: < 1 } => $"{Elapsed.Seconds}s",
        { TotalHours: < 1 } => $"{(int)Elapsed.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)Elapsed.TotalHours}h {Elapsed.Minutes}m",
        _ => $"{(int)Elapsed.TotalDays}d {Elapsed.Hours}h",
    };

    /// <summary>
    /// Color for this row's status dot and status text (2026-10-05: also now the
    /// <c>TextBlock.Foreground</c> for the status/elapsed line in <c>MainWindow.xaml</c>, not just
    /// the small dot — see IMPLEMENTATION_PLAN.md §9.6). This per-row flat-color palette
    /// (green=working, gray=idle/finished/closed) is this app's own established state palette;
    /// the actual Win32 tray icon doesn't use it — it always shows the gold glyph drawn by
    /// <c>TrayIconRenderer</c>, pulsing toward golden-white while any session is working.
    /// <see cref="SessionStatus.WaitingForInput"/> renders gray (not a distinct color) — see
    /// <see cref="StatusLabelFor"/>'s doc comment for why this status can't be confidently
    /// colorized/labeled any more specifically today.
    /// </summary>
    /// <remarks>
    /// <see cref="SessionStatus.Finished"/> used to render <c>OrangeRed</c> while an
    /// <c>IsUnread</c> flag was still true, dropping to gray once "read" — removed 2026-10-07
    /// (the user: "I don't really need the red dot behaviour in the tray icon or unread-related
    /// features", since the dismissable balloon notification already serves that acknowledgment
    /// purpose). <see cref="SessionStatus.Finished"/> is now unconditionally gray, same as Idle.
    /// </remarks>
    public Brush StatusBrush => Status switch
    {
        SessionStatus.Working => Brushes.MediumSeaGreen,
        SessionStatus.Finished => Brushes.Gray,
        SessionStatus.WaitingForInput => Brushes.Gray,
        SessionStatus.Closed => Brushes.Gray,
        _ => Brushes.Gray,
    };

    /// <summary>
    /// Plain colored dot used for every status except the "idle-like, not actively alerting"
    /// ones — <see cref="SessionStatus.WaitingForInput"/> ("Idle") and <see
    /// cref="SessionStatus.Finished"/> — which use <see cref="StatusIconSource"/>/
    /// <see cref="StatusIconVisibility"/> instead (2026-10-05, user request: "use the guanyin svg
    /// in a golden color" in place of the gray dot for Idle rows; extended the same day to
    /// Finished rows too — see <see cref="IsIdleLike"/>'s own doc comment for why). Collapsed,
    /// not just zero-size, so it never captures layout space or hit-testing it isn't needed for.
    /// </summary>
    public System.Windows.Visibility StatusDotVisibility =>
        IsIdleLike ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>See <see cref="StatusDotVisibility"/> — the inverse, shown only for idle-like rows.</summary>
    public System.Windows.Visibility StatusIconVisibility =>
        IsIdleLike ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// True for rows that are idle in substance even if <see cref="Status"/> itself still says
    /// <see cref="SessionStatus.Finished"/> — i.e. everything <see cref="StatusBrush"/> colors
    /// gray <em>except</em> <see cref="SessionStatus.Closed"/>.
    /// </summary>
    /// <remarks>
    /// Originally added 2026-10-05 to distinguish a read <c>Finished</c> row (idle-looking, still
    /// open) from a genuinely <see cref="SessionStatus.Closed"/> session (process actually ended)
    /// — conflating the two with the same plain gray dot would have erased that distinction. Since
    /// the 2026-10-07 removal of the separate "unread"/"read" tracking (see <see
    /// cref="StatusBrush"/>'s remarks), every <c>Finished</c> row is idle-like unconditionally,
    /// not just read ones — <see cref="StatusLabel"/> still correctly says "Finished", not "Idle",
    /// for this case; only the dot/icon changes, not the (accurate, permanent) text.
    /// </remarks>
    public bool IsIdleLike => Status == SessionStatus.WaitingForInput || Status == SessionStatus.Finished;

    /// <summary>
    /// The small gold bodhisattva glyph shown in place of the status dot for idle-like rows (see
    /// <see cref="IsIdleLike"/>). Reuses the existing <c>TrayIcon.Watermark.WaitingForInput.png</c>
    /// asset directly rather than baking a new one — that PNG is already a gold render of the
    /// same artwork on a transparent background (previously kept loaded-but-unused in
    /// <c>TrayViewModel</c>, earmarked for exactly this kind of future reuse), and WPF's own
    /// <c>Image</c>/<c>Stretch="Uniform"</c> downscales it cleanly at the small size this row
    /// needs, the same way the much larger popup watermark already reuses one shared bitmap at a
    /// smaller rendered size.
    /// </summary>
    public ImageSource StatusIconSource => IdleGuanyinGoldIcon;

    private static readonly ImageSource IdleGuanyinGoldIcon = LoadIdleIcon();

    private static ImageSource LoadIdleIcon()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/Assets/TrayIcon.Watermark.WaitingForInput.png"));
        image.Freeze(); // immutable + safe to share across every row's binding, same reasoning as TrayViewModel's watermark images.
        return image;
    }
}
