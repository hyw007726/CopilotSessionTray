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
/// The run-at-startup toggle, "open logs folder" action, and "resume in
/// terminal" action are the exceptions: they're real, working logic, but
/// purely OS/process integration (a Registry Run-key entry, opening a
/// folder in Explorer, and launching a terminal) — none of them reads or
/// interprets any real Copilot session data. "Mark read" and "remove" are
/// real in-memory list mutations for this demo data, but don't yet persist
/// anywhere — Phase 2 wires them to <c>IAppStateStore</c>.
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
    private TrayAggregateState _aggregateState = TrayAggregateState.NoSessions;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _runAtStartupEnabled;

    [ObservableProperty]
    private Visibility _emptyStateVisibility = Visibility.Visible;

    public int UnreadCount => Sessions.Count(s => s.IsUnread);

    /// <summary>Whether at least one session is actively working, independent of unread status.</summary>
    public bool IsAnyWorking => Sessions.Any(s => s.Status == SessionStatus.Working);

    /// <summary>
    /// Tray icon font size, compensated for system DPI scaling. H.NotifyIcon's
    /// <c>GeneratedIconSource</c> scales its <c>Size</c>/<c>TextMargin</c> by the current DPI
    /// internally, but does <em>not</em> scale <c>FontSize</c> — so a hardcoded FontSize looks
    /// right only at 100% scaling and drifts out of the centered TextMargin box at 125%/150%/etc.
    /// (very common on modern displays). Scaling it here ourselves keeps it proportionate.
    /// </summary>
    public double IconFontSize => 44.0 * (NativeMethods.GetDpiForSystem() / 96.0);

    /// <summary>
    /// Tray icon text margin, computed to precisely center whatever <see cref="IconGlyph"/> is
    /// currently showing. GeneratedIconSource always draws via a fixed rectangle (from
    /// TextMargin) using GDI+'s tight "GenericTypographic" metrics anchored top-left rather than
    /// centered, so a single static margin can't perfectly center every glyph — a narrow glyph
    /// like "1" sits further left/up than a wider one like "9+" at the exact same margin. This
    /// measures the actual glyph with the same GDI+ APIs/format the library draws with
    /// internally, then computes the margin needed to center that specific measured size.
    /// </summary>
    public Thickness IconTextMargin
    {
        get
        {
            const double canvasSize = 128; // matches GeneratedIconSource.Size default.

            if (string.IsNullOrEmpty(IconGlyph))
            {
                return new Thickness(canvasSize / 2);
            }

            using var font = new System.Drawing.Font("Segoe UI", (float)IconFontSize, System.Drawing.FontStyle.Bold);
            using var bitmap = new System.Drawing.Bitmap(1, 1);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var textSize = graphics.MeasureString(
                IconGlyph,
                font,
                new System.Drawing.SizeF((float)canvasSize, (float)canvasSize),
                System.Drawing.StringFormat.GenericTypographic);

            var left = Math.Max(0, (canvasSize - textSize.Width) / 2);
            var top = Math.Max(0, (canvasSize - textSize.Height) / 2);
            return new Thickness(left, top, left, top);
        }
    }

    /// <summary>
    /// Short glyph shown on the tray icon. The unread count always wins when there is one —
    /// that's the number you actually came here to check — falling back to a small dot only
    /// while something is actively working, and nothing at all when it's just sitting idle/empty.
    /// </summary>
    public string IconGlyph =>
        UnreadCount > 0
            ? (UnreadCount > 9 ? "9+" : UnreadCount.ToString())
            : IsAnyWorking
                ? "\u25CF" // ● — something's actively working, nothing unread yet
                : string.Empty; // idle / no sessions — nothing to report

    /// <summary>
    /// Tray icon background: green whenever anything is actively working — that takes priority
    /// over the unread accent, since "something's happening right now" is the more immediate
    /// signal — falling back to an accent for unread-but-idle, then neutral gray for nothing.
    /// The unread count is still surfaced via the <see cref="IconGlyph"/> number regardless of
    /// which color is showing, so it's never hidden just because something is also working.
    /// </summary>
    public Brush IconBrush => IsAnyWorking
        ? Brushes.MediumSeaGreen
        : UnreadCount > 0
            ? Brushes.OrangeRed
            : Brushes.Gray;

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

    /// <summary>Marks a single session as read. Phase 2: persist via <c>IAppStateStore.SaveReadMarkerAsync</c>.</summary>
    [RelayCommand]
    private void MarkSessionRead(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        session.IsUnread = false;
        RecomputeAggregateState();
    }

    /// <summary>
    /// Removes a session from this app's visible list only — never touches
    /// the underlying Copilot CLI session/files. Phase 2: persist the
    /// dismissal via <c>IAppStateStore</c> (<see cref="Core.Models.SessionReadMarker.IsDismissed"/>)
    /// so a real poll doesn't just bring it straight back. Confirms first,
    /// defaulting to "No", so an accidental click/Enter doesn't remove it.
    /// </summary>
    [RelayCommand]
    private void RemoveSession(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Remove '{session.DisplayName}' from this list?{Environment.NewLine}{Environment.NewLine}" +
            "This only removes it from view here — it does not close or delete the real Copilot session.",
            "Copilot Session Tray",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Sessions.Remove(session);
        RecomputeAggregateState();
    }

    /// <summary>
    /// Opens a new split terminal pane and resumes the session there,
    /// verbatim. Real OS process launch (like <see cref="OpenLogsFolder"/>),
    /// matching the shape <c>ISessionLauncher.ResumeInTerminalAsync</c> will
    /// use once Phase 2 implements it — but since this is fake demo data,
    /// the id won't resolve to an actual Copilot session.
    /// </summary>
    [RelayCommand]
    private void ResumeSession(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        LaunchCopilotInSplitPane($"--resume {session.Id}", session.WorkingDirectory, session.Id);
    }

    /// <summary>
    /// Opens a new split terminal pane and starts a brand new Copilot CLI
    /// session there, seeded only with a short summary of this one — not
    /// its full history. Matches the shape
    /// <c>ISessionLauncher.StartNewSessionFromSummaryAsync</c> will use once
    /// Phase 2 implements it. In this demo build the "summary" is just
    /// stitched together from this row's own display fields, standing in
    /// for what would really be Copilot CLI's own last checkpoint overview
    /// (<c>ISessionHistoryStore.GetCheckpointsAsync</c>).
    /// <summary>
    /// Opens a new split terminal pane and starts a brand new Copilot CLI
    /// session there, seeded only with <see cref="SessionItemViewModel.Summary"/>
    /// — not this session's full history. Matches the shape
    /// <c>ISessionLauncher.StartNewSessionFromSummaryAsync</c> will use once
    /// Phase 2 implements it.
    /// </summary>
    [RelayCommand]
    private void StartSessionFromSummary(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var sanitized = SanitizeForCmdExe(session.Summary);
        LaunchCopilotInSplitPane($"\"{sanitized}\"", session.WorkingDirectory, session.Id);
    }

    /// <summary>
    /// Opens the summary panel for a session — shows its AI-written summary
    /// and lets the user set a local display-name override for it. Phase 2:
    /// persist the rename via the now-extended <c>IAppStateStore</c>
    /// (<see cref="Core.Contracts.IAppStateStore.SetCustomDisplayNameAsync"/>).
    /// </summary>
    [RelayCommand]
    private void ShowSessionSummary(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        new SessionSummaryWindow(session) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>
    /// Launches <c>copilot</c> with the given arguments in a new Windows
    /// Terminal split pane. Goes through <c>cmd /k</c> rather than invoking
    /// <c>copilot</c> directly, because it resolves to a <c>.bat</c> script
    /// on this machine (confirmed via <c>Get-Command copilot</c>), not a
    /// directly-launchable <c>.exe</c> — raw process creation (which is what
    /// both <c>Process.Start</c> and <c>wt.exe</c>'s own child-process
    /// launch use) can't resolve that by bare name the way a shell can.
    /// </summary>
    /// <param name="copilotArguments">
    /// Arguments to pass to <c>copilot</c>, already safely quoted/escaped
    /// for cmd.exe if they came from arbitrary text (see
    /// <see cref="SanitizeForCmdExe"/>) — this method does not escape them
    /// itself, since a plain session id needs no escaping at all.
    /// </param>
    private static void LaunchCopilotInSplitPane(string copilotArguments, string? workingDirectory, string sessionIdForErrorMessage)
    {
        var resolvedWorkingDirectory = workingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var startInfo = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            // ArgumentList (not a single interpolated Arguments string) is used here so
            // embedded spaces in the working directory or the copilot arguments can't be
            // misread as extra wt.exe-level arguments — each item becomes exactly one
            // properly-quoted Win32 argument regardless of its contents.
            startInfo.ArgumentList.Add("split-pane");
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(resolvedWorkingDirectory);
            startInfo.ArgumentList.Add("cmd");
            startInfo.ArgumentList.Add("/k");
            startInfo.ArgumentList.Add($"copilot {copilotArguments}");

            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Couldn't launch Windows Terminal for '{sessionIdForErrorMessage}'.{Environment.NewLine}{Environment.NewLine}" +
                "(Demo build — this is a real process launch, but the fake demo session id won't resolve to an actual Copilot session.)",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Makes arbitrary text safe to embed inside a <c>cmd /k "..."</c> command line.
    /// cmd.exe has no fully reliable, universal escape for embedded double-quotes or
    /// <c>%</c> (variable expansion) in that position, so rather than fight its notoriously
    /// inconsistent quoting, this replaces the characters that would otherwise break or alter
    /// the command with safe look-alikes. Adequate for a demo/summary string; a real Phase 2
    /// implementation feeding arbitrary checkpoint text should instead use something
    /// injection-proof like PowerShell's base64 <c>-EncodedCommand</c>.
    /// </summary>
    private static string SanitizeForCmdExe(string text) =>
        text.Replace('"', '\'').Replace('%', '_').Replace('\r', ' ').Replace('\n', ' ');

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
        _demoScenarioIndex = (_demoScenarioIndex + 1) % 5;
        LoadDemoScenario(_demoScenarioIndex);
    }

    [RelayCommand]
    private void Quit() => Application.Current.Shutdown();

    /// <summary>
    /// Replaces <see cref="Sessions"/> with one of a few fixed fake scenarios, cycling on each call.
    ///
    /// IMPORTANT: this repo is pushed to the author's personal GitHub account, not an employer's —
    /// keep all demo data, fixtures, and comments limited to clearly generic placeholders (e.g.
    /// "acme/...", "octocat/...", "sample-org/...") and never real company/product/repo names.
    /// </summary>
    private void LoadDemoScenario(int index)
    {
        Sessions.Clear();

        switch (index)
        {
            case 0: // Mixed — a "typical" moment.
                Sessions.Add(new SessionItemViewModel(
                    "demo-1", "acme/widget-api", SessionStatus.Working,
                    "Investigating flaky retry logic in the checkout test suite…", TimeSpan.FromMinutes(3), isUnread: false));
                Sessions.Add(new SessionItemViewModel(
                    "demo-2", "sample-org/inventory-service", SessionStatus.Finished,
                    "Added missing test fixture reference.", TimeSpan.FromMinutes(21), isUnread: true));
                Sessions.Add(new SessionItemViewModel(
                    "demo-3", "CopilotSessionTray", SessionStatus.WaitingForInput,
                    "Reviewed IMPLEMENTATION_PLAN.md Phase 0.5 contracts.", TimeSpan.FromHours(1), isUnread: false,
                    workingDirectory: @"C:\Git\CopilotSessionTray"));
                break;

            case 1: // Everything idle.
                Sessions.Add(new SessionItemViewModel(
                    "demo-4", "acme/billing-service", SessionStatus.WaitingForInput,
                    "Waiting on next instruction.", TimeSpan.FromMinutes(9), isUnread: false));
                break;

            case 2: // Needs attention — multiple unread.
                Sessions.Add(new SessionItemViewModel(
                    "demo-5", "sample-org/reporting-tool", SessionStatus.Finished,
                    "Build succeeded, 0 errors.", TimeSpan.FromMinutes(4), isUnread: true));
                Sessions.Add(new SessionItemViewModel(
                    "demo-6", "octocat/hello-world", SessionStatus.Finished,
                    "Applied requested review changes.", TimeSpan.FromMinutes(46), isUnread: true));
                break;

            case 3: // Empty.
                break;

            case 4: // Actively working, nothing unread yet — exercises the tray icon's "working" dot.
                Sessions.Add(new SessionItemViewModel(
                    "demo-7", "sample-org/notification-service", SessionStatus.Working,
                    "Refactoring retry policy configuration…", TimeSpan.FromSeconds(45), isUnread: false));
                break;
        }

        RecomputeAggregateState();
    }

    private void RecomputeAggregateState()
    {
        // IconGlyph/IconBrush/IconTextMargin/ToolTipText depend on UnreadCount/IsAnyWorking (and
        // Sessions.Count) directly, not just on AggregateState, so they're notified explicitly
        // here rather than relying on AggregateState's [NotifyPropertyChangedFor] — which
        // wouldn't fire if the enum value happens to stay the same while those still changed.
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(IsAnyWorking));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(IconTextMargin));
        OnPropertyChanged(nameof(ToolTipText));
        EmptyStateVisibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AggregateState = Sessions.Count == 0
            ? TrayAggregateState.NoSessions
            : Sessions.Any(s => s.IsUnread)
                ? TrayAggregateState.AttentionNeeded
                : Sessions.Any(s => s.Status == SessionStatus.Working)
                    ? TrayAggregateState.Working
                    : TrayAggregateState.Idle;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
    }
}
