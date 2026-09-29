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

    private int _demoScenarioIndex;

    public TrayViewModel(
        IOpenSessionsRegistryReader liveSessionsReader,
        IProcessLivenessChecker liveProcessChecker,
        ISessionHistoryStore liveHistoryStore,
        SessionLockFileInspector liveLockFileInspector,
        ISessionLauncher sessionLauncher,
        IYoloTaskRunner yoloTaskRunner,
        IAppStateStore appStateStore)
    {
        _liveSessionsReader = liveSessionsReader;
        _liveProcessChecker = liveProcessChecker;
        _liveHistoryStore = liveHistoryStore;
        _liveLockFileInspector = liveLockFileInspector;
        _sessionLauncher = sessionLauncher;
        _yoloTaskRunner = yoloTaskRunner;
        _appStateStore = appStateStore;

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

    partial void OnIsMutedChanged(bool value)
    {
        // Phase 2: persist via IAppStateStore once it's implemented.
        // No-op for now — Phase 1 keeps this purely as an in-memory toggle
        // to prove the context menu binding works.
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
        return new SessionItemViewModel(
            summary.Id, displayName, SessionStatus.Closed, detail, now - summary.UpdatedAtUtc,
            isUnread: false, workingDirectory: summary.Cwd);
    }

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
