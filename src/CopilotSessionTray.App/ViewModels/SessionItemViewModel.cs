using System.Windows.Media;
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
    private SessionStatus _status;

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedLabel))]
    private TimeSpan _elapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadDotVisibility))]
    private bool _isUnread;

    public string StatusLabel => StatusLabelFor(Status);

    private static string StatusLabelFor(SessionStatus status) => status switch
    {
        SessionStatus.Working => "Working",
        SessionStatus.WaitingForInput => "Waiting for input",
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

    public Brush StatusBrush => Status switch
    {
        SessionStatus.Working => Brushes.DodgerBlue,
        SessionStatus.WaitingForInput => Brushes.Goldenrod,
        SessionStatus.Finished => Brushes.OrangeRed,
        SessionStatus.Closed => Brushes.Gray,
        _ => Brushes.Gray,
    };

    public System.Windows.Visibility UnreadDotVisibility =>
        IsUnread ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}
