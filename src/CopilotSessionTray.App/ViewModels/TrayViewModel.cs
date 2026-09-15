using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.App.Services;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.App.ViewModels;

/// <summary>
/// Main tray view model — Phase 1 of IMPLEMENTATION_PLAN.md §10 ("Tray
/// shell: icon, context menu, quit, run-at-startup toggle; fake/static data
/// only").
///
/// Nothing in this class reads real Copilot CLI state: <see cref="Sessions"/>
/// is seeded from <see cref="LoadDemoScenario"/> with static fake rows, and
/// <see cref="CycleDemoScenario"/> exists purely so the tray icon/popup's
/// different visual states can be exercised on demand. That real wiring
/// (polling <c>open-sessions-state.json</c>, tailing <c>events.jsonl</c>,
/// etc.) lands in later phases behind <c>CopilotSessionTray.Core</c>'s
/// already-reviewed contracts — see IMPLEMENTATION_PLAN.md §4.1.
///
/// The run-at-startup toggle and "open logs folder" action are the two
/// exceptions: they're real, working logic, but purely OS/file-system
/// integration (a Registry Run-key entry, and opening a folder in
/// Explorer) — neither reads or interprets any Copilot session data.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject
{
    private static readonly string CopilotLogsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "logs");

    private int _demoScenarioIndex;

    public TrayViewModel()
    {
        Sessions = new ObservableCollection<SessionItemViewModel>();
        LoadDemoScenario(0);

        // Read without going through the property setter so we don't
        // immediately re-write the registry with the value we just read.
        _runAtStartupEnabled = StartupRegistration.IsEnabled();
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconGlyph), nameof(IconBrush), nameof(ToolTipText))]
    private TrayAggregateState _aggregateState = TrayAggregateState.NoSessions;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _runAtStartupEnabled;

    [ObservableProperty]
    private Visibility _emptyStateVisibility = Visibility.Visible;

    public int UnreadCount => Sessions.Count(s => s.IsUnread);

    /// <summary>Short glyph shown on the tray icon: an unread count (capped) or a plain dot.</summary>
    public string IconGlyph =>
        AggregateState == TrayAggregateState.AttentionNeeded && UnreadCount > 0
            ? (UnreadCount > 9 ? "9+" : UnreadCount.ToString())
            : "\u25CF"; // ●

    public Brush IconBrush => AggregateState switch
    {
        TrayAggregateState.NoSessions => Brushes.Gray,
        TrayAggregateState.Idle => Brushes.MediumSeaGreen,
        TrayAggregateState.Working => Brushes.DodgerBlue,
        TrayAggregateState.AttentionNeeded => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    public string ToolTipText
    {
        get
        {
            if (Sessions.Count == 0)
            {
                return "Copilot Session Tray — no sessions (demo data)";
            }

            var working = Sessions.Count(s => s.Status == SessionStatus.Working);
            return $"Copilot Session Tray (demo data){Environment.NewLine}" +
                   $"{Sessions.Count} session(s) · {working} working · {UnreadCount} need attention";
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        // Phase 2: persist via IAppStateStore once it's implemented.
        // No-op for now — Phase 1 keeps this purely as an in-memory toggle
        // to prove the context menu binding works.
    }

    partial void OnRunAtStartupEnabledChanged(bool value) => StartupRegistration.SetEnabled(value);

    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var session in Sessions)
        {
            session.IsUnread = false;
        }

        RecomputeAggregateState();
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!Directory.Exists(CopilotLogsFolder))
        {
            MessageBox.Show(
                $"Copilot logs folder not found:{Environment.NewLine}{CopilotLogsFolder}{Environment.NewLine}{Environment.NewLine}" +
                "(Expected in this demo build — Phase 1 doesn't read real Copilot state.)",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CopilotLogsFolder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CycleDemoScenario()
    {
        _demoScenarioIndex = (_demoScenarioIndex + 1) % 4;
        LoadDemoScenario(_demoScenarioIndex);
    }

    [RelayCommand]
    private void Quit() => Application.Current.Shutdown();

    /// <summary>Replaces <see cref="Sessions"/> with one of a few fixed fake scenarios, cycling on each call.</summary>
    private void LoadDemoScenario(int index)
    {
        Sessions.Clear();

        switch (index)
        {
            case 0: // Mixed — a "typical" moment.
                Sessions.Add(new SessionItemViewModel(
                    "demo-1", "CubicTelecom/VerizonService", SessionStatus.Working,
                    "Investigating flaky CancelSubscription system test…", TimeSpan.FromMinutes(3), isUnread: false));
                Sessions.Add(new SessionItemViewModel(
                    "demo-2", "CubicTelecom/MnoDomain.SystemTests", SessionStatus.Finished,
                    "Added missing xRetry.Reqnroll package reference.", TimeSpan.FromMinutes(21), isUnread: true));
                Sessions.Add(new SessionItemViewModel(
                    "demo-3", "CopilotSessionTray", SessionStatus.WaitingForInput,
                    "Reviewed IMPLEMENTATION_PLAN.md Phase 0.5 contracts.", TimeSpan.FromHours(1), isUnread: false));
                break;

            case 1: // Everything idle.
                Sessions.Add(new SessionItemViewModel(
                    "demo-4", "CubicTelecom/CustomerProfile", SessionStatus.WaitingForInput,
                    "Waiting on next instruction.", TimeSpan.FromMinutes(9), isUnread: false));
                break;

            case 2: // Needs attention — multiple unread.
                Sessions.Add(new SessionItemViewModel(
                    "demo-5", "CubicTelecom/ES2Service", SessionStatus.Finished,
                    "Build succeeded, 0 errors.", TimeSpan.FromMinutes(4), isUnread: true));
                Sessions.Add(new SessionItemViewModel(
                    "demo-6", "CubicTelecom/Integration.Verizon.eUICC", SessionStatus.Finished,
                    "Applied requested review changes.", TimeSpan.FromMinutes(46), isUnread: true));
                break;

            case 3: // Empty.
                break;
        }

        RecomputeAggregateState();
    }

    private void RecomputeAggregateState()
    {
        OnPropertyChanged(nameof(UnreadCount));
        EmptyStateVisibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AggregateState = Sessions.Count == 0
            ? TrayAggregateState.NoSessions
            : Sessions.Any(s => s.IsUnread)
                ? TrayAggregateState.AttentionNeeded
                : Sessions.Any(s => s.Status == SessionStatus.Working)
                    ? TrayAggregateState.Working
                    : TrayAggregateState.Idle;
    }
}
