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
        bool isUnread,
        string? workingDirectory = null,
        string? realSummary = null)
    {
        Id = id;
        _displayName = displayName;
        _status = status;
        _detail = detail;
        _elapsed = elapsed;
        _isUnread = isUnread;
        WorkingDirectory = workingDirectory;

        // realSummary is the real Copilot CLI checkpoint overview/session title text resolved by
        // TrayViewModel.GetResumeSummaryAsync for real (non-demo) sessions — see that method's doc
        // comment for the 2026-10-01 bug this fixes: this constructor used to *always* fabricate a
        // templated sentence here regardless of whether real data was available, producing
        // nonsense like "Summary of prior work on Architect: C:\Git (last status: Closed)." for
        // real, closed sessions instead of an actual summary of what was worked on. Only demo rows
        // (which never pass realSummary) still get that fabricated placeholder — fine for them
        // since it's just exercising the UI with fake data to begin with.
        Summary = !string.IsNullOrWhiteSpace(realSummary)
            ? realSummary
            : $"Summary of prior work on {displayName}: {detail} (last status: {StatusLabelFor(status)}).";
    }

    public string Id { get; }

    /// <summary>
    /// The directory this session was started from, if known — mirrors
    /// <see cref="Core.Models.SessionSummary.Cwd"/>, used by "resume in
    /// terminal" so the new pane opens in the right place.
    /// </summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// For real sessions: the session's own most recent checkpoint overview (or, failing that,
    /// its title/summary field) — genuine Copilot CLI-written content, this app never generates
    /// it itself. For Phase 1 demo rows only: a fabricated placeholder sentence, since there's no
    /// real checkpoint data behind a fake id. Fixed at construction, independent of any later
    /// local rename via <see cref="DisplayName"/>.
    /// </summary>
    public string Summary { get; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadDotVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusDotVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusIconVisibility))]
    private bool _isUnread;

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
    /// (green=working, orange=finished-and-unread, gray=idle/closed/acknowledged) is this app's
    /// own established state palette; the actual Win32 tray icon no longer uses a matching flat
    /// color itself as of the 2026-10-06 glyph+ring+dot redesign (see <c>MainWindow.xaml.cs</c>'s
    /// <c>ComposeTrayIconBitmap</c>), but still uses the same green for its spinning "working"
    /// ring and the same red for its "unread" dot overlay, so the palette stays consistent across
    /// both surfaces even though the tray icon's own shape changed.
    /// <see cref="SessionStatus.WaitingForInput"/> renders gray (not a distinct color) — see
    /// <see cref="StatusLabelFor"/>'s doc comment for why this status can't be confidently
    /// colorized/labeled any more specifically today.
    /// </summary>
    /// <remarks>
    /// <see cref="SessionStatus.Finished"/> only renders <c>OrangeRed</c> while <see
    /// cref="IsUnread"/> is still true (2026-10-05 fix) — previously this stayed orange-red
    /// forever, even after clicking "Read", because this switch only considered <see
    /// cref="Status"/>. The orange-red color exists to signal "fresh, needs your attention";
    /// once acknowledged via "Read" (<c>TrayViewModel.MarkSessionRead</c>, which only ever
    /// flips <see cref="IsUnread"/>, never <see cref="Status"/>), that urgency is gone even
    /// though the row's status label still accurately says "Finished" — so the color drops to
    /// the same neutral gray as Idle, rather than keeping a claim ("still needs attention")
    /// that's no longer true.
    /// </remarks>
    public Brush StatusBrush => Status switch
    {
        SessionStatus.Working => Brushes.MediumSeaGreen,
        SessionStatus.Finished when IsUnread => Brushes.OrangeRed,
        SessionStatus.Finished => Brushes.Gray,
        SessionStatus.WaitingForInput => Brushes.Gray,
        SessionStatus.Closed => Brushes.Gray,
        _ => Brushes.Gray,
    };

    public System.Windows.Visibility UnreadDotVisibility =>
        IsUnread ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// Plain colored dot used for every status except the "idle-like, not actively alerting"
    /// ones — <see cref="SessionStatus.WaitingForInput"/> ("Idle") and a read
    /// <see cref="SessionStatus.Finished"/> row — which use <see cref="StatusIconSource"/>/
    /// <see cref="StatusIconVisibility"/> instead (2026-10-05, user request: "use the guanyin svg
    /// in a golden color" in place of the gray dot for Idle rows; extended the same day to
    /// read-Finished rows too — see <see cref="IsIdleLike"/>'s own doc comment for why). Collapsed,
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
    /// Added 2026-10-05, after the user pushed back on the read-Finished → gray change from
    /// earlier the same day: "when I click read, it becomes gray from red, but I think the
    /// session is still idle and not closed." Before this, a read <c>Finished</c> row used the
    /// same plain gray <c>Ellipse</c> dot as a genuinely <see cref="SessionStatus.Closed"/>
    /// session — visually conflating two different facts: "this process has actually ended"
    /// (<c>Closed</c>) versus "this finished a turn, you've seen it, nothing further is known"
    /// (read <c>Finished</c>, which is substantively the same uncertain, non-alerting situation
    /// as <c>WaitingForInput</c>/"Idle" — see that status's own doc comments on
    /// <c>TrayAggregateState.WaitingForInput</c> for why this app can't claim anything more
    /// specific). Giving both the same gold glyph (while leaving <c>Closed</c> with the plain
    /// dot) makes that distinction visible instead of silently erasing it: "idle-looking, still
    /// here" vs. "actually closed" are no longer the same gray circle. <see cref="StatusLabel"/>
    /// still correctly says "Finished", not "Idle", for this case — only the dot/icon changes,
    /// not the (accurate, permanent) text.
    /// </remarks>
    private bool IsIdleLike => Status == SessionStatus.WaitingForInput || (Status == SessionStatus.Finished && !IsUnread);

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
