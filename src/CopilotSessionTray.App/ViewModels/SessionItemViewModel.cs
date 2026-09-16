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
        string? workingDirectory = null)
    {
        Id = id;
        _displayName = displayName;
        Status = status;
        Detail = detail;
        Elapsed = elapsed;
        _isUnread = isUnread;
        WorkingDirectory = workingDirectory;

        // Demo stand-in for what Phase 2 would source from Copilot CLI's own
        // last checkpoint overview (ISessionHistoryStore.GetCheckpointsAsync)
        // — see ISessionLauncher.StartNewSessionFromSummaryAsync.
        Summary = $"Summary of prior work on {displayName}: {detail} (last status: {StatusLabelFor(status)}).";
    }

    public string Id { get; }

    /// <summary>
    /// The directory this session was started from, if known — mirrors
    /// <see cref="Core.Models.SessionSummary.Cwd"/>, used by "resume in
    /// terminal" so the new pane opens in the right place.
    /// </summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// A short AI-written recap of the session, sourced from Copilot CLI's
    /// own checkpoints in the real implementation — this app never
    /// generates it itself. Fixed at construction, independent of any later
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

    public SessionStatus Status { get; }

    public string Detail { get; }

    public TimeSpan Elapsed { get; }

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
        _ => $"{(int)Elapsed.TotalHours}h {Elapsed.Minutes}m",
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
