using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly SessionSummaryProvider _summaryProvider;

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
    private bool _isSummaryDialogOpen;

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
        SessionSummaryProvider summaryProvider,
        ISessionLauncher sessionLauncher,
        IYoloTaskRunner yoloTaskRunner,
        IAppStateStore appStateStore,
        ISessionDetectionEngine detectionEngine,
        INotificationService notificationService,
        StickerState stickerState)
    {
        _liveSessionsReader = liveSessionsReader;
        _liveProcessChecker = liveProcessChecker;
        _liveHistoryStore = liveHistoryStore;
        _liveLockFileInspector = liveLockFileInspector;
        _summaryProvider = summaryProvider;
        _sessionLauncher = sessionLauncher;
        _yoloTaskRunner = yoloTaskRunner;
        _appStateStore = appStateStore;
        _detectionEngine = detectionEngine;
        _notificationService = notificationService;
        _stickerState = stickerState;
        _sessionPins = new SessionPins(appStateStore);

        _monitorTimer = new DispatcherTimer { Interval = MonitorPollInterval };
        _monitorTimer.Tick += async (_, _) => await OnMonitorTimerTickAsync();

        // Seeded with demo scenario 0 only as a harmless placeholder in case StartMonitoring()
        // is never called (e.g. a future unit test constructing this directly with fakes, without
        // going through the real App.xaml.cs startup path) — the real app always immediately
        // replaces this with live data, below.
        Sessions = new ObservableCollection<SessionItemViewModel>();
        SessionsView = CreateSessionsView(Sessions);
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

        _ = LoadLiveSessionsThenPollAsync();
        _ = LoadMutePreferenceAsync();
        _monitorTimer.Start();
    }

    private async Task LoadLiveSessionsThenPollAsync()
    {
        await LoadStickerStateAsync();
        await LoadPinsAsync();
        await LoadLiveSessionsScenarioAsync();
        await OnMonitorTimerTickAsync();
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

    /// <summary>Whether at least one session is actively working.</summary>
    public bool IsAnyWorking => Sessions.Any(s => s.Status == SessionStatus.Working);

    /// <summary>
    /// Whether at least one open session is in the <see cref="SessionStatus.WaitingForInput"/>
    /// per-session status. Not currently used to drive any tray-icon/priority treatment — see
    /// <see cref="TrayAggregateState.WaitingForInput"/>'s doc comment for why (kept as a stub for
    /// a future real implementation, IMPLEMENTATION_PLAN.md §9.6 follow-up). Kept as its own
    /// property regardless, since it's still a reasonable thing for other code to ask.
    /// </summary>
    public bool IsAnyWaitingForInput => Sessions.Any(s => s.Status == SessionStatus.WaitingForInput);

    /// <summary>
    /// The popup's background watermark art, color-matched to <see cref="AggregateState"/>
    /// (2026-10-02/05, see IMPLEMENTATION_PLAN.md §9.5/§9.6) — applied to a plain WPF
    /// <c>Image.Source</c> binding in <c>MainWindow.xaml</c> rather than anything touching
    /// <c>H.NotifyIcon</c>/<c>GeneratedIconSource</c>. This is deliberately a completely different
    /// code path from the tray icon's own rendering (the actual Win32 tray icon is drawn directly
    /// by <c>TrayIconRenderer</c>, called from <c>MainWindow.xaml.cs</c>'s <c>UpdateTrayIcon</c> —
    /// see its own doc comment — rather than driven from any property here): a plain
    /// <see cref="System.Windows.Controls.Image"/> swapping a pre-loaded,
    /// <c>Freeze()</c>-d <see cref="BitmapImage"/> is standard, synchronous WPF image binding with
    /// no GDI+ interop and no async regeneration involved at all — so none of the concurrency
    /// risk documented on that method applies here, however often this property changes.
    /// </summary>
    /// <remarks>
    /// <b>Gold when any visible row is idle-like</b> (2026-10-07, user request: "when there's
    /// open session in golden, can you also make the watermark golden?") — below
    /// <c>Working</c> in priority (an actively-working session is still more notable than one
    /// merely sitting idle), but now a distinct tier above plain gray, matching the same gold
    /// per-row glyph <see cref="SessionItemViewModel.StatusIconSource"/> already shows for exactly
    /// these rows (<see cref="SessionItemViewModel.IsIdleLike"/> — made public specifically so
    /// this switch and that row-level one share one definition rather than risking two
    /// independently-maintained copies of "what counts as idle-like" drifting apart).
    /// This is *not* a reversal of <see cref="TrayAggregateState.WaitingForInput"/>'s own "stub,
    /// not deleted" stance below — that one is specifically about *claiming to know* a session is
    /// "awaiting a reply" (a thing Copilot CLI's data can't actually distinguish from "long done");
    /// this is just a visual consistency match for rows already shown in gold today, not a new
    /// claim about freshness/urgency.
    /// </remarks>
    public ImageSource WatermarkImageSource => AggregateState switch
    {
        TrayAggregateState.Working => WorkingWatermarkImage,
        _ => Sessions.Any(s => s.IsIdleLike) ? WaitingForInputWatermarkImage : IdleWatermarkImage,
    };

    private static readonly ImageSource WorkingWatermarkImage = LoadWatermarkImage("TrayIcon.Watermark.Working.png");
    private static readonly ImageSource IdleWatermarkImage = LoadWatermarkImage("TrayIcon.Watermark.Idle.png");

    /// <summary>
    /// The gold watermark variant — selected by <see cref="WatermarkImageSource"/> whenever any
    /// visible row is idle-like (2026-10-07 — see that property's own remarks for why this is a
    /// visual-consistency match, not a reversal of <see cref="TrayAggregateState.WaitingForInput"/>'s
    /// own "stub, not deleted" stance). Also reused on two other surfaces as the same gold bitmap:
    /// <see cref="SessionItemViewModel.StatusIconSource"/> (the per-row "Idle" icon), and — loaded
    /// independently as a GDI+ <c>System.Drawing.Bitmap</c>, not this WPF <see cref="ImageSource"/>
    /// — the actual Win32 tray icon's own base glyph in <c>MainWindow.xaml.cs</c>.
    /// </summary>
    private static readonly ImageSource WaitingForInputWatermarkImage = LoadWatermarkImage("TrayIcon.Watermark.WaitingForInput.png");

    private static ImageSource LoadWatermarkImage(string fileName)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/{fileName}"));
        image.Freeze(); // immutable + safe to share across threads, same as any static resource.
        return image;
    }

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
                   $"{Sessions.Count} session(s) · {working} working";
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

        var clearsKept = session.IsPinned || session.HasSticker;
        var result = MessageBox.Show(
            $"Remove '{session.DisplayName}' from this list?{Environment.NewLine}{Environment.NewLine}" +
            "This only removes it from view here — it does not close or delete the real Copilot session." +
            (clearsKept ? $"{Environment.NewLine}{Environment.NewLine}It is pinned or has a sticker; removing it clears those too." : string.Empty),
            "Copilot Session Tray",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await RemoveConfirmedAsync(session);
    }

    private async Task RemoveConfirmedAsync(SessionItemViewModel session)
    {
        Sessions.Remove(session);
        RecomputeAggregateState();

        if (IsShowingLiveData)
        {
            if (session.HasSticker)
            {
                SetSticker(session, false);
            }

            if (session.IsPinned)
            {
                session.IsPinned = false;
                await _sessionPins.SetPinnedAsync(session.Id, false);
            }

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

        await RefreshSummaryAsync(session);

        await TryLaunchAsync(
            () => _sessionLauncher.StartNewSessionFromSummaryAsync(session.Summary, session.WorkingDirectory), session.Id);
    }

    /// <summary>
    /// Re-reads a live session's summary and title from the history store. A read that yields less
    /// than the row already has (e.g. the database was briefly locked) is ignored.
    /// </summary>
    private async Task RefreshSummaryAsync(SessionItemViewModel session)
    {
        if (!IsShowingLiveData)
        {
            return;
        }

        SessionSummary? row = null;
        try
        {
            row = await _liveHistoryStore.GetSessionAsync(session.Id);
        }
        catch (Exception)
        {
            row = null;
        }

        var details = await _summaryProvider.GetAsync(session.Id, row);
        if (session.SummaryView is { } current && details.View.Source < current.Source)
        {
            return;
        }

        session.UpdateSummary(details.ResumeText, details.View);
        if (row is not null && ToDisplayText(details.Title) is { } title)
        {
            session.DisplayName = await ApplyCustomDisplayNameOverrideAsync(session.Id, title);
        }
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

        await RefreshSummaryAsync(session);
        var originalDisplayName = session.DisplayName;
        _isSummaryDialogOpen = true;
        try
        {
            new SessionSummaryWindow(session) { Owner = TryGetOwnerWindow() }.ShowDialog();
        }
        finally
        {
            _isSummaryDialogOpen = false;
        }

        if (IsShowingLiveData && session.DisplayName != originalDisplayName)
        {
            session.HasPlaceholderName = false;
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
    private async Task ShowDemoBalloons()
    {
        var samples = new[]
        {
            new SessionNotification("demo-balloon-1", "acme/widget-api", "Fixed the flaky retry test by awaiting the cleanup hook; all 42 checkout tests pass.", TimeSpan.FromMinutes(12)),
            new SessionNotification("demo-balloon-2", "sample-org/inventory-service", "Added the missing test fixture reference and re-ran the suite.", TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(20)),
            new SessionNotification("demo-balloon-3", "octocat/hello-world", "Blocked: needs your approval before pushing to the protected branch.", TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5)),
        };

        foreach (var sample in samples)
        {
            await _notificationService.NotifySessionFinishedAsync(sample);
            await Task.Delay(500);
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

            AddSessionIfNew(await BuildOpenSessionItemAsync(entry, now));
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
                var kept = IsKept(summary.Id);
                if (closedCount >= 10 && !kept)
                {
                    continue;
                }

                var marker = await _appStateStore.GetReadMarkerAsync(summary.Id);
                if (marker?.IsDismissed == true)
                {
                    continue; // removed; don't let it re-fill the 10-slot quota after a reload.
                }

                if (AddSessionIfNew(await BuildClosedSessionItemAsync(summary, now)) && !kept)
                {
                    closedCount++;
                }
            }
        }
        catch
        {
            // session-store.db read failed — the open-sessions list above is the more important,
            // authoritative part of this view, so just skip the closed-sessions addition rather
            // than fail the whole scenario over it.
        }

        await AddMissingPinnedSessionsAsync(now);
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
        var details = await _summaryProvider.GetAsync(entry.SessionId, summary);
        var title = ToDisplayText(details.Title);
        var computedName = title ?? details.Repository ?? details.Cwd ?? $"Session {shortId}…";
        var displayName = await ApplyCustomDisplayNameOverrideAsync(entry.SessionId, computedName);
        var detail = details.Cwd is { Length: > 0 } cwd
            ? $"{cwd} · opened {FormatAge(now - entry.OpenedAtUtc)} ago"
            : $"Opened {FormatAge(now - entry.OpenedAtUtc)} ago.";
        detail = await AppendOriginAsync(detail, details.OriginSessionId);

        return new SessionItemViewModel(
            entry.SessionId,
            displayName,
            status,
            detail,
            now - entry.RefreshedAtUtc,
            workingDirectory: details.Cwd,
            realSummary: details.ResumeText,
            summaryView: details.View)
        {
            HasPlaceholderName = title is null && displayName == computedName,
            OpenedAtUtc = entry.OpenedAtUtc,
            Gist = SessionGist.FromView(details.View) ?? string.Empty,
        };
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
        var details = await _summaryProvider.GetAsync(summary.Id, summary);
        var computedName = ToDisplayText(details.Title) ?? summary.Repository ?? summary.Cwd ?? $"Session {shortId}…";
        var displayName = await ApplyCustomDisplayNameOverrideAsync(summary.Id, computedName);
        var detail = await AppendOriginAsync(summary.Cwd ?? summary.Repository ?? "(unknown workspace)", details.OriginSessionId);
        return new SessionItemViewModel(
            summary.Id, displayName, SessionStatus.Closed, detail, now - summary.UpdatedAtUtc,
            workingDirectory: summary.Cwd, realSummary: details.ResumeText, summaryView: details.View)
        {
            OpenedAtUtc = summary.CreatedAtUtc,
            Gist = SessionGist.FromView(details.View) ?? string.Empty,
        };
    }

    private async Task<string> AppendOriginAsync(string detail, string? originSessionId)
    {
        if (originSessionId is null)
        {
            return detail;
        }

        var originTitle = ToDisplayText(await _summaryProvider.GetTitleAsync(originSessionId))
            ?? (originSessionId.Length > 8 ? originSessionId[..8] : originSessionId);
        return $"{detail} · ↪ resumed from \"{SessionText.ToSingleLine(originTitle, MaxOriginTitleLength)}\"";
    }

    /// <summary>Returns the user's persisted local rename for a session, if any, otherwise <paramref name="fallbackDisplayName"/>.</summary>
    private async Task<string> ApplyCustomDisplayNameOverrideAsync(string sessionId, string fallbackDisplayName)
    {
        var customName = await _appStateStore.GetCustomDisplayNameAsync(sessionId);
        return string.IsNullOrEmpty(customName) ? fallbackDisplayName : customName;
    }

    private const int MaxDisplayTextLength = 200;
    private const int MaxOriginTitleLength = 60;
    private const int MaxOutcomeLength = 240;
    private static readonly TimeSpan MinimumNotifiableWork = TimeSpan.FromSeconds(30);

    private static string? ToDisplayText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : SessionText.ToSingleLine(text, MaxDisplayTextLength);

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
                    "Investigating flaky retry logic in the checkout test suite…", TimeSpan.FromMinutes(3)));
                Sessions.Add(new SessionItemViewModel(
                    "demo-2", "sample-org/inventory-service", SessionStatus.Finished,
                    "Added missing test fixture reference.", TimeSpan.FromMinutes(21)));
                Sessions.Add(new SessionItemViewModel(
                    "demo-3", "CopilotSessionTray", SessionStatus.WaitingForInput,
                    "Reviewed IMPLEMENTATION_PLAN.md Phase 0.5 contracts.", TimeSpan.FromHours(1),
                    workingDirectory: @"C:\Git\CopilotSessionTray"));
                break;

            case 1: // Idle (WaitingForInput status — not confidently colorized, see IconBrush's remarks).
                Sessions.Add(new SessionItemViewModel(
                    "demo-4", "acme/billing-service", SessionStatus.WaitingForInput,
                    "Deployed the hotfix; smoke tests passed.", TimeSpan.FromMinutes(9)));
                break;

            case 2: // Multiple finished sessions — exercises several idle-like (gold) rows at once.
                Sessions.Add(new SessionItemViewModel(
                    "demo-5", "sample-org/reporting-tool", SessionStatus.Finished,
                    "Build succeeded, 0 errors.", TimeSpan.FromMinutes(4)));
                Sessions.Add(new SessionItemViewModel(
                    "demo-6", "octocat/hello-world", SessionStatus.Finished,
                    "Applied requested review changes.", TimeSpan.FromMinutes(46)));
                break;

            case 3: // Empty.
                break;

            case 4: // Actively working — exercises the tray icon's "working" glow.
                Sessions.Add(new SessionItemViewModel(
                    "demo-7", "sample-org/notification-service", SessionStatus.Working,
                    "Refactoring retry policy configuration…", TimeSpan.FromSeconds(45)));
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
        RefreshElapsedLabels();

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

            if (IsShowingLiveData)
            {
                await RefreshPlaceholderNamesAsync();
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

    private async Task RefreshPlaceholderNamesAsync()
    {
        foreach (var session in Sessions.Where(s => s.HasPlaceholderName).ToList())
        {
            string? title;
            try
            {
                title = ToDisplayText(await _summaryProvider.GetTitleAsync(session.Id));
            }
            catch
            {
                continue;
            }

            if (_isSummaryDialogOpen || !session.HasPlaceholderName)
            {
                continue;
            }

            if (title is not null)
            {
                session.DisplayName = title;
            }

            session.HasPlaceholderName = title is null && session.Status != SessionStatus.Closed;
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

        if (change.WorkedFor is { } worked && worked < MinimumNotifiableWork)
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
        var title = ToDisplayText(await _summaryProvider.GetTitleAsync(change.SessionId, summary))
            ?? summary?.Repository ?? summary?.Cwd ?? $"Session {shortId}…";
        var outcome = change.Outcome is null ? string.Empty : SessionText.ToPlainSnippet(change.Outcome, MaxOutcomeLength);
        var body = outcome.Length > 0 ? outcome : "Finished a turn and is waiting for input.";

        try
        {
            await _notificationService.NotifySessionFinishedAsync(
                new SessionNotification(change.SessionId, title, body, change.WorkedFor));
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
                    ApplyGist(existing, change);
                    if (existing.Status != SessionStatus.Closed)
                    {
                        return false;
                    }

                    existing.Status = change.Status;
                    var reopened = await _liveSessionsReader.ReadAsync();
                    if (reopened.TryGetValue(change.SessionId, out var reopenedEntry))
                    {
                        existing.OpenedAtUtc = reopenedEntry.OpenedAtUtc;
                    }

                    return true;
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

                var started = await BuildOpenSessionItemAsync(entry, now);
                ApplyGist(started, change);
                return AddSessionIfNew(started, atTop: true);

            case SessionChangeKind.WorkingStateChanged:
                if (existing is null)
                {
                    return false;
                }

                existing.Status = SessionStatus.Working;
                ApplyGist(existing, change);
                return true;

            case SessionChangeKind.Finished:
                if (existing is null)
                {
                    return false;
                }

                existing.Status = SessionStatus.Finished;
                ApplyGist(existing, change);
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

    private bool AddSessionIfNew(SessionItemViewModel item, bool atTop = false)
    {
        if (Sessions.Any(session => session.Id == item.Id))
        {
            return false;
        }

        item.IsPinned = _sessionPins.IsPinned(item.Id);
        item.HasSticker = _stickerState.IsPinned(item.Id);

        if (atTop)
        {
            Sessions.Insert(0, item);
        }
        else
        {
            Sessions.Add(item);
        }

        return true;
    }

    private void RefreshElapsedLabels()
    {
        if (!IsShowingLiveData)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var session in Sessions)
        {
            session.RefreshElapsed(now);
        }
    }

    private void RecomputeAggregateState()
    {
        EmptyStateVisibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Priority order (highest wins) — see TrayAggregateState's own doc comment.
        // TrayAggregateState.WaitingForInput is deliberately never assigned here (2026-10-05,
        // see TrayAggregateState's remarks) — kept as a stub enum value for a future real
        // implementation, not computed today, so sessions in that per-session status simply fall
        // through to Idle.
        AggregateState = Sessions.Count == 0
            ? TrayAggregateState.NoSessions
            : IsAnyWorking
                ? TrayAggregateState.Working
                : TrayAggregateState.Idle;

        // WatermarkImageSource/ToolTipText depend on IsAnyWorking/IsAnyWaitingForInput (and
        // Sessions.Count) directly, not just on AggregateState, so they're notified explicitly
        // here rather than relying on AggregateState's [NotifyPropertyChangedFor] — which
        // wouldn't fire if the enum value happens to stay the same while those still changed.
        //
        // Raised AFTER AggregateState is assigned above — not before (2026-10-05 fix, see
        // IMPLEMENTATION_PLAN.md): these used to be raised first, which meant any subscriber that
        // reads e.g. WatermarkImageSource synchronously *while handling* this notification would
        // read its getter before AggregateState had actually been updated to its new value —
        // observing the previous state, one step stale, every single time. Confirmed via a real
        // reproduction (not just reasoning): 7 of 8 rapid demo-cycle notifications showed a stale
        // AggregateState at the moment of this very notification before this reorder; 0 of 8
        // after it.
        OnPropertyChanged(nameof(IsAnyWorking));
        OnPropertyChanged(nameof(IsAnyWaitingForInput));
        OnPropertyChanged(nameof(WatermarkImageSource));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
