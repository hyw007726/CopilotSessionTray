using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
/// <b>Default view, 2026-10-01:</b> the very first thing shown (as soon as
/// <see cref="StartMonitoring"/> runs, which happens immediately after this
/// ViewModel is constructed — see <c>App.xaml.cs</c>'s <c>OnStartup</c>) is
/// now the real, live scenario (<see cref="LoadLiveSessionsScenarioAsync"/>),
/// not fake demo data. "Cycle demo data" still works exactly as before —
/// <c>_demoScenarioIndex</c> starts at <c>5</c> (the live scenario's own
/// index) instead of its implicit default of <c>0</c>, so the very same
/// 0→1→2→3→4→5→0… cycle just continues from one step further along, letting
/// demo states 0-4 still be exercised on demand without changing what
/// "Cycle" itself does.
///
/// The "Cycle demo data" scenarios (0-4) still use static fake rows via
/// <see cref="LoadDemoScenario"/>, purely so the tray icon/popup's
/// different visual states can be exercised on demand — persistence
/// (read/dismissed markers, custom display names) is deliberately never
/// applied to that fake data, so cycling stays deterministic. Scenario 5
/// (<see cref="LoadLiveSessionsScenarioAsync"/>) and the session history
/// window are real, live data.
///
/// The run-at-startup toggle, "open logs folder" action, and per-session
/// actions (resume/start-from-summary/mark read/remove/rename) are real,
/// working logic: resume/start-from-summary/start-new-task go through
/// <see cref="Core.Contracts.ISessionLauncher"/>/<see cref="Core.Contracts.IYoloTaskRunner"/>
/// (retrofitted per IMPLEMENTATION_PLAN.md §9.1 — this class used to bypass
/// them with private launch methods of its own), and mark-read/remove/rename
/// persist via <see cref="Core.Contracts.IAppStateStore"/> for real (non-demo)
/// sessions only.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject
{
    /// <summary>
    /// How often the background monitor (<see cref="StartMonitoring"/>) calls
    /// <see cref="ISessionDetectionEngine.PollAsync"/>. Chosen per the two-tier refresh design
    /// discussed while reviewing this feature: this poll itself is cheap (a small local JSON
    /// file + lock-file/process checks — no SQLite hit unless a session actually started or
    /// finished), so a short interval is fine; 3 seconds keeps the tray feeling close to
    /// real-time without meaningfully loading the machine.
    /// </summary>
    private static readonly TimeSpan MonitorPollInterval = TimeSpan.FromSeconds(3);

    private static readonly string CopilotLogsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "logs");

    // Real Core implementations (see Core\Services\), used only by the "live" cycle scenario
    // (case 5) below — everything else in this class stays Phase 1 fake/static data. Injected via
    // constructor (see App.xaml.cs's DI composition root) rather than constructed here directly —
    // this used to be `= new OpenSessionsRegistryReader()` etc. field initializers before
    // IMPLEMENTATION_PLAN.md §9.1's "no DI container" gap was addressed.
    private readonly IOpenSessionsRegistryReader _liveSessionsReader;
    private readonly IProcessLivenessChecker _liveProcessChecker;
    private readonly ISessionHistoryStore _liveHistoryStore;
    private readonly SessionLockFileInspector _liveLockFileInspector;

    // Real, working process-launch logic (resume/start-from-summary/start-new-task), and real,
    // working persistence (read/dismissed markers, custom display names, remembered workspace
    // path) — used for both the live-data scenario and the session history window, never for
    // demo data (see the class doc comment above).
    private readonly ISessionLauncher _sessionLauncher;
    private readonly IYoloTaskRunner _yoloTaskRunner;
    private readonly IAppStateStore _appStateStore;

    // Continuous background monitoring (§10 Phase 3) — the detection engine + notification
    // dispatch that runs regardless of what's currently displayed (demo scenario or live view),
    // so a finished session is noticed even while the user isn't looking at the popup at all.
    // See StartMonitoring()/OnMonitorTimerTickAsync() below for how this ties into Sessions.
    private readonly ISessionDetectionEngine _detectionEngine;
    private readonly INotificationService _notificationService;
    private readonly DispatcherTimer _monitorTimer;
    private bool _isPolling;

    // Starts at 5 (not the implicit default of 0) so the FIRST real view (triggered by
    // StartMonitoring() below) is the live scenario, not fake demo data — see the class doc
    // comment's "Default view, 2026-10-01" note. "Cycle demo data" still walks the exact same
    // 0,1,2,3,4,5,0,... sequence as before; this only changes which step the app starts on.
    private int _demoScenarioIndex = 5;

    public TrayViewModel(
        IOpenSessionsRegistryReader liveSessionsReader,
        IProcessLivenessChecker liveProcessChecker,
        ISessionHistoryStore liveHistoryStore,
        SessionLockFileInspector liveLockFileInspector,
        ISessionLauncher sessionLauncher,
        IYoloTaskRunner yoloTaskRunner,
        IAppStateStore appStateStore,
        ISessionDetectionEngine detectionEngine,
        INotificationService notificationService)
    {
        _liveSessionsReader = liveSessionsReader;
        _liveProcessChecker = liveProcessChecker;
        _liveHistoryStore = liveHistoryStore;
        _liveLockFileInspector = liveLockFileInspector;
        _sessionLauncher = sessionLauncher;
        _yoloTaskRunner = yoloTaskRunner;
        _appStateStore = appStateStore;
        _detectionEngine = detectionEngine;
        _notificationService = notificationService;

        _monitorTimer = new DispatcherTimer { Interval = MonitorPollInterval };
        _monitorTimer.Tick += async (_, _) => await OnMonitorTimerTickAsync();

        // Seeded with demo scenario 0 only as a harmless placeholder in case StartMonitoring()
        // is never called (e.g. a future unit test constructing this directly with fakes, without
        // going through the real App.xaml.cs startup path) — the real app always immediately
        // replaces this with live data, below.
        Sessions = new ObservableCollection<SessionItemViewModel>();
        LoadDemoScenario(0);

        // Read without going through the property setter so we don't
        // immediately re-write the registry with the value we just read.
        _runAtStartupEnabled = StartupRegistration.IsEnabled();
    }

    /// <summary>
    /// Starts the continuous background monitor (§10 Phase 3) — safe to call multiple times
    /// (idempotent). Deliberately not started from the constructor itself: constructing this
    /// ViewModel should stay a pure, side-effect-free operation (e.g. for future unit tests with
    /// fake dependencies), so the one real call site (<c>App.xaml.cs</c>'s <c>OnStartup</c>) opts
    /// into monitoring explicitly once the whole app is otherwise ready. Also responsible for
    /// replacing the constructor's placeholder demo data with the real live scenario — see the
    /// class doc comment's "Default view, 2026-10-01" note.
    /// </summary>
    public void StartMonitoring()
    {
        if (_monitorTimer.IsEnabled)
        {
            return;
        }

        _ = LoadLiveSessionsScenarioAsync();
        _ = LoadMutePreferenceAsync();
        _monitorTimer.Start();
        _ = OnMonitorTimerTickAsync(); // poll once immediately rather than waiting a full interval.
    }

    /// <summary>Loads the persisted mute preference at startup — otherwise <see cref="IsMuted"/> would always reset to its default (false) on every launch.</summary>
    private async Task LoadMutePreferenceAsync()
    {
        var preferences = await _appStateStore.GetPreferencesAsync();
        IsMuted = preferences.IsMuted;
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
    [NotifyPropertyChangedFor(nameof(TrayMenuHeaderText))]
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

    /// <summary>
    /// Bold header line shown at the top of the tray's right-click context menu. Was previously a
    /// static XAML string hardcoded to "(demo data)" — stale/wrong as soon as <see cref="IsShowingLiveData"/>
    /// scenario 5 was added, since it never updated to say so. Mirrors <see cref="HeaderSubtitleText"/>'s
    /// live/demo distinction so the menu can't show information that contradicts the popup below it.
    /// </summary>
    public string TrayMenuHeaderText => IsShowingLiveData
        ? "Copilot Session Tray (live)"
        : "Copilot Session Tray (demo data)";

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

    /// <summary>
    /// Persists the mute toggle via <see cref="IAppStateStore"/> — until 2026-09-29 this was a
    /// pure in-memory toggle with a "Phase 2" placeholder comment; now that the background
    /// monitor (<see cref="StartMonitoring"/>) needs a real, durable mute setting to gate
    /// notifications on, it's wired up for real.
    /// </summary>
    partial void OnIsMutedChanged(bool value)
    {
        _ = PersistMutePreferenceAsync(value);
    }

    private async Task PersistMutePreferenceAsync(bool isMuted)
    {
        var preferences = await _appStateStore.GetPreferencesAsync();
        await _appStateStore.SavePreferencesAsync(preferences with { IsMuted = isMuted });
    }

    partial void OnRunAtStartupEnabledChanged(bool value) => StartupRegistration.SetEnabled(value);

    [RelayCommand]
    private async Task MarkAllRead()
    {
        foreach (var session in Sessions)
        {
            session.IsUnread = false;
        }

        RecomputeAggregateState();

        if (IsShowingLiveData)
        {
            foreach (var session in Sessions)
            {
                await PersistReadMarkerAsync(session.Id, isDismissed: false);
            }
        }
    }

    /// <summary>Marks a single session as read, persisting via <see cref="IAppStateStore"/> for real (non-demo) sessions.</summary>
    [RelayCommand]
    private async Task MarkSessionRead(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        session.IsUnread = false;
        RecomputeAggregateState();

        if (IsShowingLiveData)
        {
            await PersistReadMarkerAsync(session.Id, isDismissed: false);
        }
    }

    /// <summary>
    /// Removes a session from this app's visible list only — never touches the underlying
    /// Copilot CLI session/files. For real (non-demo) sessions, persists the dismissal via
    /// <see cref="IAppStateStore"/> (<see cref="Core.Models.SessionReadMarker.IsDismissed"/>) so
    /// reloading the live scenario doesn't just bring it straight back; demo-data dismissals stay
    /// in-memory only, so cycling stays deterministic. Confirms first, defaulting to "No", so an
    /// accidental click/Enter doesn't remove it.
    /// </summary>
    [RelayCommand]
    private async Task RemoveSession(SessionItemViewModel? session)
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

        if (IsShowingLiveData)
        {
            await PersistReadMarkerAsync(session.Id, isDismissed: true);
        }
    }

    /// <summary>
    /// Loads any existing read marker for <paramref name="sessionId"/> and re-saves it with
    /// <paramref name="isDismissed"/> applied, preserving whatever else was already recorded (so
    /// dismissing a session doesn't erase its acknowledgement time, and marking one read doesn't
    /// un-dismiss it). Only ever called for real sessions — see the class doc comment.
    /// </summary>
    private async Task PersistReadMarkerAsync(string sessionId, bool isDismissed)
    {
        var existing = await _appStateStore.GetReadMarkerAsync(sessionId);
        var marker = existing is null
            ? new SessionReadMarker(sessionId, LastAcknowledgedEventOffset: 0, DateTimeOffset.UtcNow, isDismissed)
            : existing with { LastAcknowledgedUtc = DateTimeOffset.UtcNow, IsDismissed = isDismissed };
        await _appStateStore.SaveReadMarkerAsync(marker);
    }

    /// <summary>
    /// Opens a new split terminal pane and resumes the session there, verbatim, via
    /// <see cref="ISessionLauncher.ResumeInTerminalAsync"/> — since this can also run against fake
    /// demo data (cycle scenarios 0-4), a demo session's id won't resolve to an actual Copilot
    /// session, but the process-launch attempt itself is real either way.
    /// </summary>
    [RelayCommand]
    private async Task ResumeSession(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        await TryLaunchAsync(
            () => _sessionLauncher.ResumeInTerminalAsync(session.Id, session.WorkingDirectory), session.Id);
    }

    /// <summary>
    /// Opens a new terminal tab/pane and starts a brand new Copilot CLI session there, via
    /// <see cref="ISessionLauncher.StartNewSessionFromSummaryAsync"/>, seeded only with
    /// <see cref="SessionItemViewModel.Summary"/> — not this session's full history.
    /// </summary>
    [RelayCommand]
    private async Task StartSessionFromSummary(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        await TryLaunchAsync(
            () => _sessionLauncher.StartNewSessionFromSummaryAsync(session.Summary, session.WorkingDirectory), session.Id);
    }

    /// <summary>
    /// Opens the summary panel for a session — shows its AI-written summary and lets the user set
    /// a local display-name override for it. For real (non-demo) sessions, persists a changed name
    /// via <see cref="IAppStateStore.SetCustomDisplayNameAsync"/> once the dialog closes.
    /// </summary>
    [RelayCommand]
    private async Task ShowSessionSummary(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var originalDisplayName = session.DisplayName;
        new SessionSummaryWindow(session) { Owner = TryGetOwnerWindow() }.ShowDialog();

        if (IsShowingLiveData && session.DisplayName != originalDisplayName)
        {
            await _appStateStore.SetCustomDisplayNameAsync(session.Id, session.DisplayName);
        }
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
        var items = new List<SessionItemViewModel>(recentSessions.Count);
        foreach (var summary in recentSessions)
        {
            items.Add(await BuildClosedSessionItemAsync(summary, now));
        }

        new SessionHistoryWindow(items, this) { Owner = TryGetOwnerWindow() }.ShowDialog();
    }

    /// <summary>
    /// Opens the "new task" panel (prompt + workspace picker, pre-filled from the last-used
    /// workspace via <see cref="IAppStateStore"/>), then starts it via
    /// <see cref="IYoloTaskRunner.StartInteractiveAsync"/> on "Start".
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

        await TryLaunchAsync(
            () => _yoloTaskRunner.StartInteractiveAsync(window.Prompt, window.WorkspaceDirectory, window.EnableAllPermissions),
            "new task");
    }

    /// <summary>
    /// Runs a real process-launch action from <see cref="ISessionLauncher"/>/<see cref="IYoloTaskRunner"/>
    /// (both retrofitted per IMPLEMENTATION_PLAN.md §9.1 — this used to be a private
    /// <c>LaunchCopilotInTerminal</c> method here with its own try/catch; the launch logic itself
    /// now lives in <c>Core.Services.CopilotTerminalLauncher</c>, shared by both interfaces),
    /// showing the same error dialog on failure as before if Windows Terminal can't be started.
    /// </summary>
    /// <param name="launch">The launch action to run.</param>
    /// <param name="context">A short label identifying what was being launched, used only in the error message.</param>
    private static async Task TryLaunchAsync(Func<Task> launch, string context)
    {
        try
        {
            await launch();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Couldn't launch Windows Terminal for '{context}'.{Environment.NewLine}{Environment.NewLine}" +
                "Make sure Windows Terminal (wt.exe) is installed.",
                "Copilot Session Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

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
            var marker = await _appStateStore.GetReadMarkerAsync(entry.SessionId);
            if (marker?.IsDismissed == true)
            {
                continue; // user removed this one; don't let a reload bring it straight back.
            }

            Sessions.Add(await BuildOpenSessionItemAsync(entry, now));
        }

        // Also append the most recent genuinely-closed sessions below the open ones — same
        // ISessionHistoryStore query ShowSessionHistory uses, just filtered down to a handful and
        // excluding anything already shown above as open, so nothing appears twice.
        var openIds = new HashSet<string>(genuinelyOpen.Select(entry => entry.SessionId));
        try
        {
            var recentSessions = await _liveHistoryStore.GetRecentSessionsAsync(maxCount: 30 + openIds.Count);
            var closedCount = 0;
            foreach (var summary in recentSessions.Where(s => !openIds.Contains(s.Id)))
            {
                if (closedCount >= 10)
                {
                    break;
                }

                var marker = await _appStateStore.GetReadMarkerAsync(summary.Id);
                if (marker?.IsDismissed == true)
                {
                    continue; // removed; don't let it re-fill the 10-slot quota after a reload.
                }

                Sessions.Add(await BuildClosedSessionItemAsync(summary, now));
                closedCount++;
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
    /// Builds a display row for a genuinely-open session from a registry entry — shared by
    /// <see cref="LoadLiveSessionsScenarioAsync"/>'s initial load and
    /// <see cref="OnMonitorTimerTickAsync"/>'s background monitor (a <see cref="SessionChangeKind.Started"/>
    /// transition, while the live view is showing), so both use identical
    /// title-resolution/rename-override/formatting logic. Callers are responsible for any
    /// dismissal-marker check first (this method always builds the row).
    /// </summary>
    private async Task<SessionItemViewModel> BuildOpenSessionItemAsync(OpenSessionEntry entry, DateTimeOffset now)
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
        var computedName = summary?.Summary ?? summary?.Repository ?? summary?.Cwd ?? $"Session {shortId}…";
        var displayName = await ApplyCustomDisplayNameOverrideAsync(entry.SessionId, computedName);
        var detail = summary?.Cwd is { Length: > 0 } cwd
            ? $"{cwd} · opened {FormatAge(now - entry.OpenedAtUtc)} ago"
            : $"Opened {FormatAge(now - entry.OpenedAtUtc)} ago.";
        var realSummary = await GetResumeSummaryAsync(entry.SessionId, summary?.Summary);

        return new SessionItemViewModel(
            entry.SessionId,
            displayName,
            status,
            detail,
            now - entry.RefreshedAtUtc,
            isUnread: false,
            workingDirectory: summary?.Cwd,
            realSummary: realSummary);
    }

    /// <summary>
    /// Builds a display row for a closed/historical session from <see cref="ISessionHistoryStore"/>
    /// data alone (no live registry entry) — shared by <see cref="ShowSessionHistory"/> and the
    /// "recent closed sessions" appended below the open ones in
    /// <see cref="LoadLiveSessionsScenarioAsync"/>, so both use identical fallback/formatting
    /// logic rather than two copies that could quietly drift apart. Applies any persisted custom
    /// display name (see <see cref="ApplyCustomDisplayNameOverrideAsync"/>) — deliberately not
    /// dismissal-filtered here, unlike the live scenario's closed-sessions block above: the
    /// history window is meant to still show everything, even sessions "removed" from the
    /// quick-glance popup.
    /// </summary>
    private async Task<SessionItemViewModel> BuildClosedSessionItemAsync(SessionSummary summary, DateTimeOffset now)
    {
        var shortId = summary.Id.Length > 8 ? summary.Id[..8] : summary.Id;
        var computedName = summary.Summary ?? summary.Repository ?? summary.Cwd ?? $"Session {shortId}…";
        var displayName = await ApplyCustomDisplayNameOverrideAsync(summary.Id, computedName);
        var detail = summary.Cwd ?? summary.Repository ?? "(unknown workspace)";
        var realSummary = await GetResumeSummaryAsync(summary.Id, summary.Summary);
        return new SessionItemViewModel(
            summary.Id, displayName, SessionStatus.Closed, detail, now - summary.UpdatedAtUtc,
            isUnread: false, workingDirectory: summary.Cwd, realSummary: realSummary);
    }

    /// <summary>
    /// Resolves the real text to seed a "Resume with Summary" new session with — per
    /// <c>ISessionLauncher.StartNewSessionFromSummaryAsync</c>'s always-documented intent: the
    /// session's most recent <see cref="SessionCheckpoint.Overview"/> (Copilot CLI already writes
    /// these during a session — confirmed real, substantive content via a real session's own
    /// checkpoints while fixing this). Falls back, in order, to a recap built from the session's
    /// own real <see cref="SessionTurn"/> history (see <see cref="BuildTurnsFallbackSummary"/>),
    /// then its bare title/summary field, and finally a plain "no summary yet" message.
    ///
    /// Fixes a real 2026-10-01 bug: despite that interface doc comment always describing this
    /// design, nothing ever actually called <see cref="ISessionHistoryStore.GetCheckpointsAsync"/>
    /// — <c>SessionItemViewModel</c>'s constructor instead *always* fabricated a templated
    /// sentence from the display name/detail/status, even for real sessions, producing nonsense
    /// like "Summary of prior work on Architect: C:\Git (last status: Closed)." — which read like
    /// a wrong/garbled working directory but was actually just a fake Phase 1 placeholder string
    /// that had leaked into real usage once real data was wired up.
    ///
    /// Second bug, same day: that first fix still produced a near-useless prompt
    /// ("Summarize Architect Work") when resuming a session that has <em>no checkpoint at all</em>
    /// — many short sessions never reach one — since it fell straight through to the session's
    /// bare auto-generated title, which carries essentially no real content for a brand new
    /// session (with zero prior context) to act on; confirmed directly against the real session
    /// this happened for (a short, single-turn session whose only real content — a user message
    /// and the assistant's one reply — was never being read at all). Fixed by trying a real
    /// <see cref="ISessionHistoryStore.GetTurnsAsync"/>-based recap first in that case, so even a
    /// session with no checkpoint still forwards its actual first question and most recent answer
    /// — directly from the session's own history — rather than just a short disconnected label.
    ///
    /// Third fix, same feature: the result never carried the original session's own identity
    /// forward at all — reported directly against a real Copilot CLI screenshot showing its own
    /// <c>/resume</c> tip ("Switch sessions by local or cloud history ID, task ID, or name") right
    /// above a freshly-resumed-with-summary session with none of those three things visible
    /// anywhere. Every tier below now runs its result through
    /// <see cref="BuildSummaryWithProvenanceHeader"/>, which prepends the real session id (and
    /// title, if known) plus an explicit <c>/resume &lt;id&gt;</c> hint — so a human reading the
    /// new session's transcript (or the summary panel, which shares this same text) can always
    /// trace it back to, or jump straight into, the original, regardless of which fallback tier
    /// produced the body underneath it.
    ///
    /// Truncated defensively: unlike a checkpoint overview (consistently a few hundred/thousand
    /// characters in every real checkpoint inspected), the <paramref name="fallbackSummary"/> this
    /// falls back to can occasionally be a session's entire raw system prompt verbatim (observed
    /// for some automated/scripted session types) — many KB of text would overflow cmd.exe's
    /// ~8191-character single-line limit once embedded in the generated launch script's
    /// <c>copilot -i "..."</c> line, silently breaking the launch. Truncation is applied to the
    /// body only, before the (always-short) provenance header is added on top.
    /// </summary>
    private async Task<string> GetResumeSummaryAsync(string sessionId, string? fallbackSummary)
    {
        try
        {
            var checkpoints = await _liveHistoryStore.GetCheckpointsAsync(sessionId);
            var latestOverview = checkpoints.Count > 0 ? checkpoints[^1].Overview : null;
            if (!string.IsNullOrWhiteSpace(latestOverview))
            {
                return BuildSummaryWithProvenanceHeader(sessionId, fallbackSummary, TruncateForResumePrompt(latestOverview));
            }
        }
        catch
        {
            // checkpoints table read failed (e.g. momentarily locked) — fall through to the
            // turns-based recap below rather than fail the whole row over it.
        }

        try
        {
            var turns = await _liveHistoryStore.GetTurnsAsync(sessionId);
            var turnsRecap = BuildTurnsFallbackSummary(turns);
            if (!string.IsNullOrWhiteSpace(turnsRecap))
            {
                return BuildSummaryWithProvenanceHeader(sessionId, fallbackSummary, TruncateForResumePrompt(turnsRecap));
            }
        }
        catch
        {
            // turns table read failed — fall through to the bare title/summary field below.
        }

        var body = string.IsNullOrWhiteSpace(fallbackSummary)
            ? "No summary is available for this session yet."
            : TruncateForResumePrompt(fallbackSummary);
        return BuildSummaryWithProvenanceHeader(sessionId, fallbackSummary, body);
    }

    /// <summary>
    /// Prepends a short, always-present header identifying the real originating session —
    /// id (the same value this app's own "⤴ Resume" → "Resume with history" action already passes
    /// to <c>copilot --resume=&lt;id&gt;</c>, so it's confirmed resumable this same way via the
    /// real Copilot CLI's own <c>/resume &lt;id&gt;</c> slash command too) and title/name, if
    /// known — to <paramref name="body"/> (whatever <see cref="GetResumeSummaryAsync"/>'s fallback
    /// tier produced). Addresses the user's 2026-10-01 report, with a real screenshot of Copilot
    /// CLI's own <c>/resume</c> tip, that a resumed-with-summary session carried no trace of
    /// "history ID, task ID, or name" at all — this app only has the first of those three
    /// (CLI "task"/cloud-agent ids are a different, cloud-side concept this app's local-only scope
    /// never reads), so that's what's surfaced. Framed explicitly as context <em>about</em> the
    /// prompt (not part of it) so the receiving agent reads it as background, not an instruction
    /// to act on immediately — and so a human skimming the transcript later can copy the id
    /// straight into a fresh <c>/resume</c> without parsing prose to find it.
    /// </summary>
    private static string BuildSummaryWithProvenanceHeader(string sessionId, string? title, string body)
    {
        // The title is meant to be a short identifying label, not content — but the real
        // SessionSummary.Summary field reused as this parameter can itself occasionally be a
        // session's entire raw system prompt verbatim (see the class's own remarks on
        // fallbackSummary above). Bounded separately and far more tightly than the body's own
        // TruncateForResumePrompt, so that same edge case can't sneak an unbounded multi-KB blob
        // back in through this "title" path instead.
        const int maxTitleLength = 80;
        var trimmedTitle = title?.Trim();
        var titleSuffix = string.IsNullOrWhiteSpace(trimmedTitle)
            ? string.Empty
            : $", titled \"{(trimmedTitle!.Length <= maxTitleLength ? trimmedTitle : trimmedTitle[..maxTitleLength] + "…")}\"";

        var header = $"[Context: this is a condensed summary of an earlier Copilot CLI session{titleSuffix} " +
            $"(id: {sessionId}). It is not itself part of this request — if the full original conversation " +
            $"is ever needed, it can be resumed directly with `/resume {sessionId}`.]";
        return $"{header}{Environment.NewLine}{Environment.NewLine}{body}";
    }

    /// <summary>
    /// Builds a short recap directly from a session's real <see cref="SessionTurn"/> history, for
    /// <see cref="GetResumeSummaryAsync"/>'s middle fallback tier (sessions with real conversation
    /// but no checkpoint yet) — the first turn's user message (what was actually asked) plus the
    /// last turn's assistant response (the most recent real outcome), rather than every turn
    /// verbatim, which could be large for a long, checkpoint-less session. Returns null if there
    /// are no turns, or if every turn is completely empty, so the caller can fall through to its
    /// own next tier instead of seeding a new session with just whitespace/an empty string.
    /// </summary>
    private static string? BuildTurnsFallbackSummary(IReadOnlyList<SessionTurn> turns)
    {
        if (turns.Count == 0)
        {
            return null;
        }

        var firstUserMessage = turns[0].UserMessage;
        var lastAssistantResponse = turns[^1].AssistantResponse;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(firstUserMessage))
        {
            parts.Add($"Originally asked: {firstUserMessage}");
        }

        if (!string.IsNullOrWhiteSpace(lastAssistantResponse))
        {
            parts.Add($"Most recent outcome: {lastAssistantResponse}");
        }

        return parts.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, parts) : null;
    }

    private const int MaxResumeSummaryLength = 4000;

    private static string TruncateForResumePrompt(string text) =>
        text.Length <= MaxResumeSummaryLength ? text : text[..MaxResumeSummaryLength] + " … (truncated)";

    /// <summary>Returns the user's persisted local rename for a session, if any, otherwise <paramref name="fallbackDisplayName"/>.</summary>
    private async Task<string> ApplyCustomDisplayNameOverrideAsync(string sessionId, string fallbackDisplayName)
    {
        var customName = await _appStateStore.GetCustomDisplayNameAsync(sessionId);
        return string.IsNullOrEmpty(customName) ? fallbackDisplayName : customName;
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

    /// <summary>
    /// One tick of the continuous background monitor (§10 Phase 3) — runs on
    /// <see cref="_monitorTimer"/>'s interval regardless of whether the popup is even open or
    /// which demo scenario (if any) is currently displayed, since noticing a finished session
    /// while the user isn't looking is this app's whole reason to exist (§1).
    /// </summary>
    private async Task OnMonitorTimerTickAsync()
    {
        if (_isPolling)
        {
            return; // previous tick's poll is still running (e.g. a slow SQLite read) —
                     // ISessionDetectionEngine.PollAsync is documented as not reentrant-safe, so
                     // skip this tick entirely rather than risk overlapping calls.
        }

        _isPolling = true;
        try
        {
            IReadOnlyList<SessionChangeEvent> changes;
            try
            {
                changes = await _detectionEngine.PollAsync();
            }
            catch
            {
                return; // transient read failure past the reader's own retry budget — skip this
                         // tick; the next one tries again from scratch.
            }

            if (changes.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var mutatedVisibleList = false;

            foreach (var change in changes)
            {
                if (change.Kind == SessionChangeKind.Finished)
                {
                    await NotifyIfNotMutedAsync(change);
                }

                if (IsShowingLiveData && await ApplyChangeToVisibleSessionsAsync(change, now))
                {
                    mutatedVisibleList = true;
                }
            }

            if (mutatedVisibleList)
            {
                RecomputeAggregateState();
            }
        }
        finally
        {
            _isPolling = false;
        }
    }

    /// <summary>
    /// Dispatches a real notification for a <see cref="SessionChangeKind.Finished"/> transition,
    /// unless the user has muted notifications — checked fresh from <see cref="IAppStateStore"/>
    /// each time (not cached) so toggling mute takes effect immediately, including for a poll
    /// already in flight.
    /// </summary>
    private async Task NotifyIfNotMutedAsync(SessionChangeEvent change)
    {
        var preferences = await _appStateStore.GetPreferencesAsync();
        if (preferences.IsMuted)
        {
            return;
        }

        SessionSummary? summary = null;
        try
        {
            summary = await _liveHistoryStore.GetSessionAsync(change.SessionId);
        }
        catch
        {
            // best-effort enrichment only — still notify with a generic title below rather than
            // skip the notification entirely over a lookup failure.
        }

        var shortId = change.SessionId.Length > 8 ? change.SessionId[..8] : change.SessionId;
        var title = summary?.Repository ?? summary?.Cwd ?? $"Session {shortId}…";
        var body = summary?.Summary ?? "Finished a turn and is waiting for input.";

        try
        {
            await _notificationService.NotifySessionFinishedAsync(
                new SessionNotification(change.SessionId, title, body, ElapsedWorking: null));
        }
        catch
        {
            // a notification dispatch failure (e.g. no tray icon attached yet) must never break
            // the poll loop itself.
        }
    }

    /// <summary>
    /// Patches <see cref="Sessions"/> to reflect one <see cref="SessionChangeEvent"/> — only
    /// called while <see cref="IsShowingLiveData"/>, so a background transition never corrupts
    /// whatever fake demo scenario is currently on screen. Returns whether anything actually
    /// changed, so the caller only pays for <see cref="RecomputeAggregateState"/> when needed.
    /// </summary>
    private async Task<bool> ApplyChangeToVisibleSessionsAsync(SessionChangeEvent change, DateTimeOffset now)
    {
        var existing = Sessions.FirstOrDefault(s => s.Id == change.SessionId);

        switch (change.Kind)
        {
            case SessionChangeKind.Started:
                if (existing is not null)
                {
                    return false; // already visible (e.g. as a "closed" history row) — leave it.
                }

                var marker = await _appStateStore.GetReadMarkerAsync(change.SessionId);
                if (marker?.IsDismissed == true)
                {
                    return false; // user removed this one; a background poll shouldn't bring it back either.
                }

                var registry = await _liveSessionsReader.ReadAsync();
                if (!registry.TryGetValue(change.SessionId, out var entry))
                {
                    return false; // shouldn't normally happen (the engine just reported this from
                                    // the same source moments ago) — skip defensively rather than guess.
                }

                Sessions.Insert(0, await BuildOpenSessionItemAsync(entry, now));
                return true;

            case SessionChangeKind.WorkingStateChanged:
                if (existing is null)
                {
                    return false;
                }

                existing.Status = SessionStatus.Working;
                return true;

            case SessionChangeKind.Finished:
                if (existing is null)
                {
                    return false;
                }

                existing.Status = SessionStatus.Finished;
                existing.IsUnread = true;
                return true;

            case SessionChangeKind.Closed:
                if (existing is null)
                {
                    return false;
                }

                // Kept visible (not removed) with its status flipped, so the user can see "that
                // one just closed" rather than having a row silently vanish without explanation.
                existing.Status = SessionStatus.Closed;
                return true;

            default:
                return false;
        }
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
