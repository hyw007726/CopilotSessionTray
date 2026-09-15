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
        bool isUnread)
    {
        Id = id;
        DisplayName = displayName;
        Status = status;
        Detail = detail;
        Elapsed = elapsed;
        _isUnread = isUnread;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public SessionStatus Status { get; }

    public string Detail { get; }

    public TimeSpan Elapsed { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadDotVisibility))]
    private bool _isUnread;

    public string StatusLabel => Status switch
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
