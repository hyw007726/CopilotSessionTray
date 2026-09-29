using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionTray.App.Services;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

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

    // Real Core implementations (see Core\Services\), used only by the "live" cycle scenario
    // (case 5) below — everything else in this class stays Phase 1 fake/static data. Constructed
    // directly here rather than via DI, since no DI container exists yet in this Phase 1 skeleton.
    private readonly IOpenSessionsRegistryReader _liveSessionsReader = new OpenSessionsRegistryReader();
    private readonly IProcessLivenessChecker _liveProcessChecker = new ProcessLivenessChecker();
    private readonly ISessionHistoryStore _liveHistoryStore = new SessionHistoryStore();
    private readonly SessionLockFileInspector _liveLockFileInspector = new();

    // Used for real, working persistence (remembering the "Start new task" workspace path) —
    // not just the live-data preview scenario like the four fields above.
    private readonly IAppStateStore _appStateStore = new AppStateStore();

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

    /// <summary>
    /// Whether <see cref="Sessions"/> currently holds real data read live from this machine's
    /// <c>%USERPROFILE%\.copilot\open-sessions-state.json</c> (cycle scenario 5) rather than one
    /// of the fixed Phase 1 fake scenarios (0-4). Purely a presentation flag for
    /// <see cref="HeaderSubtitleText"/>/<see cref="EmptyStateText"/> — it doesn't change how
    /// Sessions itself is bound/rendered.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitleText))]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private bool _isShowingLiveData;

    /// <summary>Set only when the live scenario's real read fails, to surface why the list came back empty.</summary>
    private string? _liveDataError;

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

    /// <summary>Subtitle shown under the popup's title — makes it obvious at a glance whether <see cref="Sessions"/> is fake demo data or a real live read.</summary>
    public string HeaderSubtitleText => IsShowingLiveData
        ? "🟢 LIVE — read from your real %USERPROFILE%\\.copilot\\open-sessions-state.json"
        : "Phase 1 demo — static/fake data, not wired to real sessions yet";

    /// <summary>Text shown in the popup when <see cref="Sessions"/> is empty — distinguishes "no fake demo rows for this scenario" from "your real registry has no open sessions right now" / a real read failure.</summary>
    public string EmptyStateText => IsShowingLiveData
        ? (_liveDataError ?? "No open sessions found in your real .copilot registry right now.")
        : "No sessions (demo)";

    public string ToolTipText
    {
        get
        {
            var dataLabel = IsShowingLiveData ? "live data" : "demo data";

            if (Sessions.Count == 0)
            {
                return $"Copilot Session Tray — no sessions ({dataLabel})";
            }

            var working = Sessions.Count(s => s.Status == SessionStatus.Working);
            return $"Copilot Session Tray ({dataLabel}){Environment.NewLine}" +
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

        LaunchCopilotInTerminal($"--resume={session.Id}", session.WorkingDirectory, session.Id);
    }

    /// <summary>
    /// Opens a new terminal tab/pane and starts a brand new Copilot CLI
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
        LaunchCopilotInTerminal($"-i \"{sanitized}\"", session.WorkingDirectory, session.Id);
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

        new SessionSummaryWindow(session) { Owner = TryGetOwnerWindow() }.ShowDialog();
    }

    /// <summary>
    /// Opens the session history window — a real, working first cut of the "searchable history
    /// view" planned in IMPLEMENTATION_PLAN.md §5/Phase 6 (no search/filter yet, just the most
    /// recently updated sessions from <see cref="ISessionHistoryStore"/>). Deliberately a
    /// separate window/command from the "Cycle demo data" scenarios: that mechanism cycles
    /// through fixed fake test data for exercising tray icon/list states, not a real data view —
    /// mixing the two would make an already-temporary Phase 1 testing affordance even more
    /// confusing to eventually remove.
    /// </summary>
    [RelayCommand]
    private async Task ShowSessionHistory()
    {
        IReadOnlyList<SessionSummary> recentSessions;
        try
        {
            recentSessions = await _liveHistoryStore.GetRecentSessionsAsync(maxCount: 30);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't read session history: {ex.Message}",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var items = recentSessions.Select(summary => BuildClosedSessionItem(summary, now)).ToList();

        new SessionHistoryWindow(items, this) { Owner = TryGetOwnerWindow() }.ShowDialog();
    }

    /// <summary>
    /// Opens the "new task" panel (prompt + workspace picker, pre-filled from the last-used
    /// workspace via <see cref="IAppStateStore"/>), matching the shape
    /// <c>IYoloTaskRunner.StartInteractiveAsync</c> will use once Phase 2 implements it — see
    /// IMPLEMENTATION_PLAN.md. Real OS process launch on "Start", like <see cref="ResumeSession"/>.
    /// </summary>
    [RelayCommand]
    private async Task StartYoloTask()
    {
        var lastWorkspace = (await _appStateStore.GetPreferencesAsync()).LastNewTaskWorkspaceDirectory;

        var window = new NewTaskWindow(lastWorkspace) { Owner = TryGetOwnerWindow() };
        if (window.ShowDialog() != true)
        {
            return; // user cancelled.
        }

        var preferences = await _appStateStore.GetPreferencesAsync();
        await _appStateStore.SavePreferencesAsync(preferences with { LastNewTaskWorkspaceDirectory = window.WorkspaceDirectory });

        var sanitized = SanitizeForCmdExe(window.Prompt);
        LaunchCopilotInTerminal($"-i \"{sanitized}\"", window.WorkspaceDirectory, "new task", window.EnableAllPermissions);
    }

    /// <summary>
    /// Launches <c>copilot</c> with the given arguments in a new Windows Terminal tab/pane.
    ///
    /// Three things here were fixed after being caught by real usage, not just code review — all
    /// verified empirically with a throwaway fake <c>copilot.cmd</c> stand-in that echoed back
    /// exactly what it received, so the whole pipeline could be checked without ever invoking a
    /// real (and non-free) agent run:
    /// <list type="bullet">
    /// <item>
    /// <b>An extra blank pane appeared.</b> The previous version used
    /// <c>wt.exe split-pane ...</c>, which — confirmed by inspecting the actual child processes
    /// wt.exe spawned — creates a brand-new window with its own default-profile pane <em>first</em>
    /// whenever no Windows Terminal window is already open, then splits the requested pane in
    /// next to it; <c>-w 0</c> does not prevent this in that cold-start case. Switched to the
    /// plain <c>wt.exe -d &lt;dir&gt; cmd /k &lt;path&gt;</c> form (no <c>split-pane</c> at all),
    /// verified to produce exactly one pane whether or not a window was already open.
    /// </item>
    /// <item>
    /// <b><c>copilot</c> failed with "unrecognized subcommand".</b> A bare positional prompt
    /// (e.g. <c>copilot "some prompt"</c>) is not a supported way to seed a session — confirmed
    /// directly from <c>copilot --help</c>: prompts need an explicit <c>-i/--interactive</c>
    /// (open an interactive session with this as the first message) or <c>-p/--prompt</c>
    /// (run once, non-interactively, then exit) flag; callers of this method now include
    /// whichever is appropriate in <paramref name="copilotArguments"/>. Separately, threading
    /// the whole command through <c>cmd /k "copilot ... \"prompt\""</c> as one embedded string
    /// also produced genuinely broken nested quoting once inspected via the real child process's
    /// command line (confirmed via <c>Win32_Process</c>) — fixed by writing the full command to a
    /// small temp <c>.cmd</c> script and having <c>cmd /k</c> just run that file by path, so only
    /// a single, simple path (not an arbitrary prompt) ever has to survive wt.exe's own
    /// command-line relay.
    /// </item>
    /// <item>
    /// <b>Still prompted for folder trust despite "enable all permissions".</b> Confirmed via
    /// <c>copilot help environment</c>: the <c>--allow-all</c>/<c>--yolo</c> command-line flags
    /// and the <c>COPILOT_ALLOW_ALL</c> environment variable are <em>not</em> equivalent — only
    /// setting the environment variable to exactly <c>"true"</c> <em>also</em> trusts the working
    /// directory without prompting; the command-line flags (and any other truthy spelling of the
    /// env var) only auto-approve individual tool/path/url actions, a separate, narrower gate.
    /// Switched <paramref name="enableAllPermissions"/> to set the environment variable (via a
    /// <c>set</c> line in the generated script, applying to the <c>copilot</c> child process
    /// specifically rather than relying on <see cref="ProcessStartInfo.EnvironmentVariables"/>
    /// with <c>UseShellExecute=true</c>, which doesn't reliably support per-launch overrides)
    /// instead of appending the command-line flag.
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="copilotArguments">
    /// Arguments to pass to <c>copilot</c>, already safely quoted/escaped for a batch-file line
    /// if they came from arbitrary text (see <see cref="SanitizeForCmdExe"/>) — this method does
    /// not escape them itself, since a plain session id needs no escaping at all.
    /// </param>
    /// <param name="context">A short label identifying what's being launched, used only in the error message if launching fails.</param>
    /// <param name="enableAllPermissions">
    /// When true, sets <c>COPILOT_ALLOW_ALL=true</c> for the launched <c>copilot</c> process —
    /// auto-approves tool/path/url actions <em>and</em> trusts the working directory without
    /// prompting (see the "Still prompted for folder trust" remark above).
    /// </param>
    private static void LaunchCopilotInTerminal(
        string copilotArguments, string? workingDirectory, string context, bool enableAllPermissions = false)
    {
        var resolvedWorkingDirectory = workingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            // Left in %TEMP% rather than deleted right after launch: cmd /k keeps running (and
            // could still need to re-read the file) for as long as the pane stays open, which has
            // no fixed end time — a stray handful of tiny leftover .cmd files is an acceptable
            // trade-off for a personal tool versus the complexity of tracking pane lifetime.
            var scriptPath = Path.Combine(Path.GetTempPath(), $"copilot-launch-{Guid.NewGuid():N}.cmd");
            var envSetupLine = enableAllPermissions ? $"set COPILOT_ALLOW_ALL=true{Environment.NewLine}" : string.Empty;
            File.WriteAllText(scriptPath, $"@echo off{Environment.NewLine}{envSetupLine}copilot {copilotArguments}{Environment.NewLine}");

            var startInfo = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            // ArgumentList (not a single interpolated Arguments string) is used here so embedded
            // spaces in the working directory or script path can't be misread as extra
            // wt.exe-level arguments — each item becomes exactly one properly-quoted Win32
            // argument regardless of its contents.
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(resolvedWorkingDirectory);
            startInfo.ArgumentList.Add("cmd");
            startInfo.ArgumentList.Add("/k");
            startInfo.ArgumentList.Add(scriptPath);

            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Couldn't launch Windows Terminal for '{context}'.{Environment.NewLine}{Environment.NewLine}" +
                "(Demo build — this is a real process launch; a fake demo session id won't resolve to an actual Copilot session.)",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Makes arbitrary text safe to embed inside a single quoted argument on a generated
    /// batch-file command line (see <see cref="LaunchCopilotInTerminal"/>). cmd.exe has no fully
    /// reliable, universal escape for embedded double-quotes or <c>%</c> (variable expansion) in
    /// that position, so rather than fight its notoriously inconsistent quoting, this replaces
    /// the characters that would otherwise break or alter the command with safe look-alikes.
    /// Adequate for a demo/summary string; a real Phase 2 implementation feeding arbitrary
    /// checkpoint text should instead use something injection-proof like PowerShell's base64
    /// <c>-EncodedCommand</c>.
    /// </summary>
    private static string SanitizeForCmdExe(string text) =>
        text.Replace('"', '\'').Replace('%', '_').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Returns <see cref="Application.MainWindow"/> if it's safe to use as a dialog's
    /// <see cref="Window.Owner"/>, or null otherwise.
    ///
    /// Found by real usage, not code review: WPF's <c>Window.Owner</c> setter throws
    /// <see cref="InvalidOperationException"/> ("Cannot set Owner property to a Window that has
    /// not been shown previously") if the target window's Win32 handle doesn't exist yet — and
    /// our own <c>MainWindow</c> is deliberately never <c>Show()</c>n at startup (see
    /// <c>App.xaml.cs</c>), only on the user's first single left-click. A double-click (which
    /// intentionally skips that single-click path — see <c>MainWindow.xaml.cs</c>'s debounce) or
    /// the tray context menu's "Start new task…" being the user's very first interaction both
    /// reach a dialog-opening command before <c>MainWindow</c> has ever been shown, and an
    /// unhandled exception on the dispatcher thread here doesn't just fail quietly — reproduced
    /// via a faithful test using the real <c>App</c> class: it propagates out of
    /// <c>Application.Run()</c> entirely and takes down the whole process, which is exactly what
    /// looked like "the app quit" rather than an error dialog. Checking the handle first avoids
    /// the crash; the dialog just opens unowned (un-centered on anything, shows in Alt+Tab) in
    /// that specific case, which is a minor cosmetic trade-off next to a full crash.
    /// </summary>
    private static Window? TryGetOwnerWindow()
    {
        var mainWindow = Application.Current.MainWindow;
        return mainWindow is not null && new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle != IntPtr.Zero
            ? mainWindow
            : null;
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
    private async Task CycleDemoScenario()
    {
        _demoScenarioIndex = (_demoScenarioIndex + 1) % 6;

        if (_demoScenarioIndex == 5)
        {
            await LoadLiveSessionsScenarioAsync();
        }
        else
        {
            IsShowingLiveData = false;
            LoadDemoScenario(_demoScenarioIndex);
        }
    }

    [RelayCommand]
    private void Quit() => Application.Current.Shutdown();

    /// <summary>
    /// Replaces <see cref="Sessions"/> with a real, live snapshot of this machine's genuinely-open
    /// Copilot CLI sessions — cycle scenario 5, answering "which sessions are in use right now"
    /// from real data instead of fake demo rows.
    ///
    /// Presence in <c>open-sessions-state.json</c> alone is <em>not</em> sufficient to mean a
    /// session is actually still open — verified empirically: entries can sit there for hours
    /// after their terminal/tab is done with them, and a single long-lived <c>copilot</c> process
    /// can hold <c>inuse.&lt;pid&gt;.lock</c> files in <em>multiple</em> old session folders at
    /// once (e.g. after <c>/resume</c>/<c>/fork</c>), with only the most-recently-refreshed one
    /// actually current. So a session only counts as genuinely open here if: (1) its
    /// <c>inuse.&lt;pid&gt;.lock</c> file exists and that pid is currently running
    /// (<see cref="SessionLockFileInspector"/> + <see cref="IProcessLivenessChecker"/> — this
    /// also directly implements the §9 "stuck state machine" mitigation, since a crashed process
    /// fails this check), and (2) it's the freshest such session for that pid — older ones sharing
    /// the same pid are treated as superseded, not open.
    ///
    /// Each surviving session's real title comes from <see cref="ISessionHistoryStore"/>'s
    /// <c>summary</c> column in <c>session-store.db</c> (confirmed to match what other Copilot
    /// surfaces, e.g. VS Code's session list, display for the same session).
    ///
    /// Below the open rows, also appends up to 10 of the most recently updated genuinely-closed
    /// sessions (same history query as <see cref="ShowSessionHistory"/>, minus anything already
    /// shown above as open) via <see cref="BuildClosedSessionItem"/> — so this one view answers
    /// both "what's open right now" and "what did I just finish", without needing the separate
    /// history window for a quick glance.
    /// </summary>
    private async Task LoadLiveSessionsScenarioAsync()
    {
        IsShowingLiveData = true;
        _liveDataError = null;
        Sessions.Clear();

        IReadOnlyDictionary<string, OpenSessionEntry> registry;
        try
        {
            registry = await _liveSessionsReader.ReadAsync();
        }
        catch (Exception ex)
        {
            _liveDataError = $"Couldn't read the real .copilot registry: {ex.Message}";
            RecomputeAggregateState();
            return;
        }

        var runningPids = new HashSet<int>(_liveProcessChecker.GetRunningCopilotProcessIds());

        // Keep only entries whose lock file points at a pid that's actually still running...
        var genuinelyLocked = registry.Values
            .Select(entry => (Entry: entry, OwningPid: _liveLockFileInspector.GetOwningProcessId(entry.SessionId)))
            .Where(x => x.OwningPid.HasValue && runningPids.Contains(x.OwningPid.Value));

        // ...then, per pid, keep only the most-recently-refreshed session — a pid can hold locks
        // in several old session folders it's since moved past, and only the freshest is current.
        var genuinelyOpen = genuinelyLocked
            .GroupBy(x => x.OwningPid!.Value)
            .Select(group => group.OrderByDescending(x => x.Entry.RefreshedAtUtc).First().Entry)
            .OrderByDescending(entry => entry.RefreshedAtUtc)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in genuinelyOpen)
        {
            var status = entry.Working ? SessionStatus.Working : SessionStatus.WaitingForInput;

            SessionSummary? summary = null;
            try
            {
                summary = await _liveHistoryStore.GetSessionAsync(entry.SessionId);
            }
            catch
            {
                // session-store.db read failed (e.g. momentarily locked past the retry budget) —
                // fall back to id-only display below rather than dropping the row entirely.
            }

            var shortId = entry.SessionId.Length > 8 ? entry.SessionId[..8] : entry.SessionId;
            var displayName = summary?.Summary ?? summary?.Repository ?? summary?.Cwd ?? $"Session {shortId}…";
            var detail = summary?.Cwd is { Length: > 0 } cwd
                ? $"{cwd} · opened {FormatAge(now - entry.OpenedAtUtc)} ago"
                : $"Opened {FormatAge(now - entry.OpenedAtUtc)} ago.";

            Sessions.Add(new SessionItemViewModel(
                entry.SessionId,
                displayName,
                status,
                detail,
                now - entry.RefreshedAtUtc,
                isUnread: false,
                workingDirectory: summary?.Cwd));
        }

        // Also append the most recent genuinely-closed sessions below the open ones — same
        // ISessionHistoryStore query ShowSessionHistory uses, just filtered down to a handful and
        // excluding anything already shown above as open, so nothing appears twice.
        var openIds = new HashSet<string>(genuinelyOpen.Select(entry => entry.SessionId));
        try
        {
            var recentSessions = await _liveHistoryStore.GetRecentSessionsAsync(maxCount: 30 + openIds.Count);
            foreach (var summary in recentSessions.Where(s => !openIds.Contains(s.Id)).Take(10))
            {
                Sessions.Add(BuildClosedSessionItem(summary, now));
            }
        }
        catch
        {
            // session-store.db read failed — the open-sessions list above is the more important,
            // authoritative part of this view, so just skip the closed-sessions addition rather
            // than fail the whole scenario over it.
        }

        RecomputeAggregateState();
    }

    /// <summary>
    /// Builds a display row for a closed/historical session from <see cref="ISessionHistoryStore"/>
    /// data alone (no live registry entry) — shared by <see cref="ShowSessionHistory"/> and the
    /// "recent closed sessions" appended below the open ones in
    /// <see cref="LoadLiveSessionsScenarioAsync"/>, so both use identical fallback/formatting
    /// logic rather than two copies that could quietly drift apart.
    /// </summary>
    private static SessionItemViewModel BuildClosedSessionItem(SessionSummary summary, DateTimeOffset now)
    {
        var shortId = summary.Id.Length > 8 ? summary.Id[..8] : summary.Id;
        var displayName = summary.Summary ?? summary.Repository ?? summary.Cwd ?? $"Session {shortId}…";
        var detail = summary.Cwd ?? summary.Repository ?? "(unknown workspace)";
        return new SessionItemViewModel(
            summary.Id, displayName, SessionStatus.Closed, detail, now - summary.UpdatedAtUtc,
            isUnread: false, workingDirectory: summary.Cwd);
    }

    private static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h {age.Minutes}m",
        _ => $"{(int)age.TotalDays}d {age.Hours}h",
    };

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
