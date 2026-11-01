# Copilot Session Tray — Implementation Plan

Status: **Phase 1 done; Phase 2 complete; Phase 3 (detection engine wire-up) and Phase 4 (notifications) now substantially complete — 64 xUnit tests passing, all 9 Core interfaces implemented, and a real continuous background monitor now drives the tray icon/unread badge/notifications end-to-end, verified against this machine's real, currently-open Copilot sessions. See §9.1/§10 Phase 3 for the 2026-09-29 notification-mechanism decision (made autonomously — user unavailable to confirm — and how it was verified. Phase 0.5 contracts still pending your review.**
Scope: personal local-use desktop utility (Windows)

> **⚠️ Privacy reminder (applies to every phase, not just today's demo data):**
> this repo is hosted on the author's **personal** GitHub account, not an
> employer's. Demo/fake data, test fixtures, code comments, commit messages,
> and screenshots must never reference real company names, internal
> repos/projects, or other employer-identifying details. Use clearly generic
> placeholders instead (e.g. `acme/...`, `octocat/...`, `sample-org/...`).

## 1. Problem statement

GitHub Copilot CLI sessions run as independent background processes with no
persistent UI. Today the only way to know "is Copilot working right now?" or
"did that long-running session finish while I wasn't looking?" is to switch
back to each terminal window. This app is a small always-on system tray
utility that:

1. Shows at a glance whether Copilot CLI is running at all.
2. Shows, per session, whether it's actively "thinking" or idle/finished.
3. Raises a Windows notification + an "unread" badge when a session finishes
   a turn/task while unattended, until the user acknowledges it.

Out of scope (v1): remote/multi-machine sync, mobile companion, editing or
resuming sessions from the tray (Copilot CLI has no IPC/API for that today —
see §7 limitations), non-Windows platforms.

## 2. Research findings (grounding for this plan)

Investigated the local machine's `%USERPROFILE%\.copilot\` folder (this is
where GitHub Copilot CLI keeps all local state) to base the design on real,
observed artifacts rather than guesses. **All of this is undocumented,
reverse-engineered behavior of Copilot CLI v1.0.83 and may change in future
releases** — see risks in §9.

### 2.1 Folder layout (`%USERPROFILE%\.copilot\`)

| Path | Purpose |
|---|---|
| `session-store.db` (+ `-wal`/`-shm`) | Central SQLite DB, WAL mode. History of all sessions/turns/checkpoints. ~29 MB / 472 sessions observed. |
| `open-sessions-state.json` | Live registry of **currently open** sessions and whether each is actively working. Rewritten frequently. |
| `session-state\<sessionId>\` | Per-session working folder (see §2.4). |
| `logs\` | `copilot.log`, per-process `process-<epochMs>-<pid>.log`, `taskbar-presence.log` (see §2.5). |
| `config.json`, `settings.json`, `mcp-config.json`, `permissions-config.json` | User configuration, not needed for v1. |

### 2.2 `session-store.db` schema (observed via `sqlite3`/PRAGMA)

Tables: `schema_version`, `sessions`, `turns`, `checkpoints`, `session_files`,
`session_refs`, `search_index*` (FTS5), `dynamic_context_items`,
`forge_trajectory_events`, `forge_skill_proposals`, `assistant_usage_events`.

Key columns:
- `sessions`: `id, cwd, repository, host_type, branch, summary, created_at, updated_at`
- `turns`: `id, session_id, turn_index, user_message, assistant_response, timestamp`
- `checkpoints`: `id, session_id, checkpoint_number, title, overview, history, work_done, technical_details, important_files, next_steps, created_at`

This is a good source for **history / session metadata** (title, repo, last
update) but is not a real-time "is it running" signal — it's the durable
record, and it's written in WAL mode by the live process, so it must only
ever be opened **read-only** from our app.

### 2.3 `open-sessions-state.json` — the "is it running" signal

```json
{
  "<sessionId>": {
    "schemaVersion": 1,
    "openedAt": "2026-09-14T09:45:44.355Z",
    "working": true,
    "refreshedAt": "2026-09-14T09:47:56.369Z"
  }
}
```

- One entry per currently-open CLI session (removed/stale when a session
  closes; needs cross-checking, see §2.4).
- `working: true` = the agent is actively processing (running a turn/tool).
- `working: false` = idle — either waiting on the user, or the process ended
  without cleaning up its entry (must cross-check liveness, §2.4).
- `refreshedAt` updates roughly every user turn / heartbeat while the process
  is alive — a stale `refreshedAt` is a secondary signal a process died.

### 2.4 `session-state\<sessionId>\` — per-session detail

| File/dir | Purpose |
|---|---|
| `inuse.<pid>.lock` | Presence + embedded PID tells us which OS process currently owns this session. Cross-check `Process.GetProcessById(pid)` to confirm liveness (handles crashes where the lock/registry entry is stale). |
| `events.jsonl` | Append-only newline-delimited JSON event stream. Observed event `type`s: `assistant.turn_start`, `assistant.message`, `tool.execution_start`, `tool.execution_complete`, `assistant.turn_end`, `session.task_complete`, `session.usage_checkpoint`, `session.shutdown`. **This is the best real-time "finished" signal** — a new `assistant.turn_end` or `session.task_complete` line is the completion event to notify on; `session.shutdown` marks a clean process exit. |
| `checkpoints\index.md` | Human-readable checkpoint summaries — good tooltip/preview content. |
| `workspace.yaml` | Session's working directory / workspace metadata. |
| `session.db` | A per-session SQLite file also exists (purpose not fully explored — treat as a Phase 0 spike item, not a v1 dependency). |

### 2.5 Process detection

Each interactive CLI session is its own `copilot.exe` process (Windows SEA
build, currently under
`%APPDATA%\npm\node_modules\@github\copilot\node_modules\@github\copilot-win32-x64\copilot.exe`).
Enumerating processes by name is a cheap coarse "is Copilot running at all"
check; combining with the PID embedded in each `inuse.<pid>.lock` gives an
exact session ↔ process mapping.

### 2.6 Important discovery: an official tray feature may already be coming

`logs\taskbar-presence.log` shows Copilot CLI **already contains an embryonic
"taskbar presence" feature**, currently disabled behind a `TASKBAR_PRESENCE`
flag and a "Forerunner build" requirement (sidecars
`copilot-taskbar-activator.exe`, `GitHubCopilotCLI.sparse.msix` are present
but unused). Implication for this plan:
- GitHub may ship an official equivalent later — design this app so it's
  trivially removable (no modifications to any `.copilot` file, read-only
  access only, all of our own state kept in a separate app-data folder).
- Don't attempt to touch/enable that hidden flag — build our own independent
  observer instead.

### 2.7 No native "unread" concept

Nothing in the observed state tracks "has the user seen this yet" — that is
purely a concept we must own. Plan: keep our own small local store (separate
from `.copilot`) mapping `sessionId → lastAcknowledgedEventOffset /
lastSeenTurnIndex`, updated when the user views/dismisses a session in the
tray UI.

## 3. State model

- **App-level status** (tray icon): `NoSessions` / `Idle` (sessions open, all
  idle) / `Working` (≥1 session actively processing) / `AttentionNeeded`
  (≥1 unread finished session) — attention-needed takes visual priority.
- **Per-session status**: `Working` → `WaitingForInput` (finished, not yet
  read) → `Read` (acknowledged) → `Closed` (process gone / `session.shutdown`
  seen).
- **Signal authority (see §9 "stuck state" risk)**: `open-sessions-state.json`'s
  own `working`/`refreshedAt` fields (+ `IProcessLivenessChecker`) are the
  authoritative source for whether a session is *currently* Working —
  `events.jsonl` is used for richer detail (what it's doing, unread
  detection) but must never be the sole way a session leaves the Working
  state, so a missed/unexpected event type can't leave it stuck.
- **Unread** = a `WaitingForInput` session the user hasn't opened/dismissed
  in the tray since its last completion event.

Detection = diffing consecutive polls of `open-sessions-state.json` +
tailing each open session's `events.jsonl`, reconciled with live process
list.

## 4. Proposed architecture

**Stack: .NET 10, C#, WPF host (MVVM) using `H.NotifyIcon.Wpf` for the tray icon.**

Rationale: this workspace is 100% .NET/C# already (VS 2022/2026 installed,
`dotnet` tooling, xUnit conventions all in place). Originally WinForms was
proposed as the simplest tray shell, but since a **settings panel is planned**
(and later a history/search view + usage dashboard, §5), WPF is the better
fit long-term:
- Data binding + MVVM make a settings UI (mute toggles, watch-list editor,
  quiet hours, notification prefs) far cleaner than WinForms' manual
  event-wiring, and scale better as options grow.
- The planned `Core` → state → UI flow maps naturally onto MVVM: `Core`
  emits state, a WPF ViewModel binds to it, the View stays declarative XAML
  — keeping UI logic testable without touching the UI layer.
- Templated/virtualized lists (session popup, history/search results, usage
  charts) are much more natural in WPF than WinForms.
- The tray icon gap is a non-issue: `H.NotifyIcon.Wpf` (actively maintained)
  gives a fully WPF-native, XAML-bindable tray icon + context menu + toast,
  so there's no need to mix in WinForms just for `NotifyIcon`.
- Cost is small: one extra NuGet dependency and `App.xaml` ceremony, which
  pays for itself as soon as the settings panel exists.

(Alternatives considered: **WinForms** — simpler for a tray-icon-only MVP,
but settings/history UI would likely need rework later; **Tauri** — smallest
binary but introduces a new Rust toolchain the shop doesn't otherwise use;
**Electron** — unnecessary weight/memory for a single-machine utility. WPF is
the pragmatic default given the settings-panel roadmap; flagged as an
assumption to revisit if you disagree.)

Layers:
- **`CopilotSessionTray.Core`** — no UI dependencies, fully unit-testable:
  - File readers/pollers for `open-sessions-state.json`, `events.jsonl`,
    `session-state\*\inuse.*.lock`.
  - Read-only `Microsoft.Data.Sqlite` accessor for `session-store.db`
    (`Mode=ReadOnly` connection string; retry with backoff on
    `SQLITE_BUSY`/locked errors; never write).
  - Process liveness checks (`Process.GetProcessById`,
    `Process.GetProcessesByName("copilot")`).
  - The detection/diff engine producing state-change events (session started
    / finished / closed) from raw polls.
  - Our own local app-state store (unread markers, preferences) — likely a
    single small SQLite file or JSON under
    `%LOCALAPPDATA%\CopilotSessionTray\`.
- **`CopilotSessionTray.App`** — WPF shell (MVVM): `H.NotifyIcon.Wpf` tray
  icon, icon-state rendering, context menu, popup session list, toast
  notifications, settings window. ViewModels bind to `Core` state; the shell
  itself stays thin and delegates all logic to `Core`.
- **`CopilotSessionTray.Core.Tests`** — xUnit tests against `Core`, using
  fixture files (sample `open-sessions-state.json`, sample `events.jsonl`)
  instead of the real `.copilot` folder, so tests are deterministic.

Update strategy: `FileSystemWatcher` on `open-sessions-state.json` and each
open session's `events.jsonl` for near-real-time reaction, **plus** a coarse
fallback poll (e.g. every 10s) since `FileSystemWatcher` is known to coalesce
or miss rapid successive writes. Debounce watcher callbacks (~300–500ms).

### 4.1 Development approach: contracts (interfaces) before implementation

To keep this reviewable and understandable as it's built, `Core` will be
designed **interface-first**: the interfaces below (plus their supporting
data models — plain records/POCOs, not interfaces) are written and reviewed
*before* any concrete implementation. Once approved, implementation behind
each interface is comparatively mechanical and can be reviewed/landed
independently, one at a time.

Proposed `Core` contracts:

| Interface | Responsibility |
|---|---|
| `IOpenSessionsRegistryReader` | Read/watch `open-sessions-state.json`; expose current `working`/`refreshedAt` per session id. |
| `ISessionEventStreamReader` | Tail a session's `events.jsonl` from a given offset; yield new events (`turn_end`, `task_complete`, `shutdown`, ...). **Implemented and wired** (`Services.SessionEventStreamReader`, driven by `SessionDetectionEngine`'s continuous poll) — see §10 Phase 3. |
| `ISessionHistoryStore` | Read-only queries against `session-store.db` (`sessions`, `turns`, `checkpoints`) for metadata/history/search. |
| `IProcessLivenessChecker` | Given a PID (from an `inuse.<pid>.lock` file), report whether the owning process is alive; list running `copilot` processes. |
| `IAppStateStore` | Persist/retrieve our own state: last-seen markers per session, mute/watch-list/quiet-hours preferences — separate from `.copilot`. |
| `ISessionDetectionEngine` | The "brain": consumes the readers above, diffs successive polls, and emits state-change events (`SessionStarted`, `SessionWorkingChanged`, `SessionFinished`, `SessionClosed`). Most important contract to review closely since it defines the whole detection model. **Implemented and wired** (`Services.SessionDetectionEngine`) — driven continuously by `TrayViewModel.StartMonitoring()`'s 3s timer for the app's entire lifetime, see §9.1/§10 Phase 3. |
| `INotificationService` | Dispatch a toast/notification for a finished session; fakeable in tests, swappable if the notification mechanism changes later. **Implemented and wired** (`Services.NotificationService` — one floating, click-to-pop `BalloonWindow` per finished session; it started as the classic `TaskbarIcon.ShowNotification` balloon, see §9.1/§10 Phase 3 for that mechanism decision, made autonomously since the user was unavailable to confirm, and §9.7's follow-ups for how and why it changed). It also raises a red alert balloon with the Windows notification sound for a session that is waiting for the user, and takes it down again (`NotifySessionNeedsInputAsync` / `DismissNeedsInputAsync`, see the 2026-11-01 follow-up at the end of §9). |
| `ISessionLauncher` *(added post-Phase 0.5)* | Resume a session, or start a new one seeded from a summary, in an external terminal. **Implemented** (`Services.SessionLauncher`) and called through by `TrayViewModel` — see §9.1/§10 Phase 2. |
| `IYoloTaskRunner` *(added post-Phase 0.5)* | Run an unattended "start new task" request (prompt + workspace + permission level) and report back a `YoloTaskResult`. **Implemented** (`Services.YoloTaskRunner`), including the previously-unused `RunHeadlessAsync` — see §9.1/§10 Phase 2. |

Deliberately **not** interfaced: data models (`SessionInfo`, `SessionEvent`,
`SessionStatus`, etc.) stay as plain records — interfaces are only added
where there's a real seam (external/mutable state, or something that needs
to be faked in tests or swapped later), not on every type.

Each interface will ship with XML doc comments describing behavior,
threading expectations, and error handling (e.g. what happens on a locked
file or a missing process) so a review can happen from the contracts alone,
without reading implementation code.

## 5. Features

### MVP
1. Tray icon reflecting aggregate state (idle / working / attention-needed,
   e.g. via color or a small badge count).
2. Hover tooltip / click popup listing open + recently-finished sessions:
   repository or cwd, status, last message/checkpoint snippet, elapsed time.
3. Windows toast notification when a session transitions
   `Working → WaitingForInput` (or a `session.task_complete` event appears),
   with a short summary (last assistant message / checkpoint title).
4. Unread badge count on the tray icon; opening/dismissing a session in the
   popup marks it read. **Done for real sessions**: `MarkSessionRead`/
   `MarkAllRead`/`RemoveSession` persist via `IAppStateStore` (demo scenarios
   deliberately stay in-memory-only, so cycling stays deterministic) — see §9.1.
5. Context menu: mute/unmute notifications, open `.copilot` logs folder,
   quit.

### Stretch / later
6. Watch-list filter by repository/cwd (only notify for chosen projects).
7. Searchable history view backed by `session-store.db` (`turns`,
   `checkpoints`, `search_index` FTS table already present). **Started**:
   `SessionHistoryWindow` shows the 30 most recently updated real sessions
   (via `ISessionHistoryStore.GetRecentSessionsAsync`), each resumable
   through the same choice menu as the tray popup's rows (factored out into
   a shared `ResumeMenuHelper` so the two don't duplicate that logic). Not
   yet included: search/filter against `search_index`, pagination beyond
   30, and drilling into a session's own `turns`/`checkpoints`.
8. Quiet hours / do-not-disturb schedule.
9. Auto-start with Windows (Startup shortcut or Run key) + single-instance
   guard (named `Mutex`).
10. Toast action buttons: "copy `copilot --resume <id>` command", "open
    working directory in Explorer/VS Code".
11. Optional token-usage/cost mini-dashboard from `assistant_usage_events`.
12. **Start a new Copilot task from the tray** — not originally listed here,
    but built during Phase 2: `NewTaskWindow` (prompt, workspace picker that
    remembers the last-used directory via `IAppStateStore`, an "enable all
    permissions" checkbox that sets `COPILOT_ALLOW_ALL=true` for the
    launched process), reachable via tray double-click or the context menu's
    "Start new task…". Real, working process launch through `IYoloTaskRunner`
    (see §9.1).
13. **Per-session quick actions** — also built during Phase 2, also not
    originally listed here: "⤴ Resume" (choice of resume-with-history or
    resume-with-summary, both launching a real terminal via `ISessionLauncher`),
    "🗒 Summary" (view the AI-written summary, set a local rename — persisted
    via `IAppStateStore` for real sessions, see item 4 above), "✕ Remove"
    (confirm-first, view-only dismissal, also persisted). Shared between
    the tray popup and `SessionHistoryWindow` via `ResumeMenuHelper`.

## 6. Notification content sketch

- **Title**: repository/folder name + short session id suffix.
- **Body**: truncated latest assistant message or checkpoint title + elapsed
  "working" duration.
- **Actions** (Windows toast buttons): Copy resume command · Open folder ·
  Dismiss.

## 7. Known limitations

- Copilot CLI has no IPC/API to "jump into" or control a session from
  outside — the tray app is **observe-only**. Any "resume" action just
  shells out `copilot --resume <id>` in a new terminal or copies the command
  to the clipboard.
- Detection is reverse-engineered from on-disk artifacts, not a supported
  API — see risks below.

## 8. Proposed project layout

```
CopilotSessionTray\
  IMPLEMENTATION_PLAN.md          <- this file
  README.md                       <- (added when implementation starts)
  .github\workflows\
    ci.yml                        <- build + test on push/PR (windows-latest)
    release.yml                   <- on `v*.*.*` tag: publish self-contained
                                      single-file win-x64, zip, attach to a
                                      GitHub Release
  src\
    CopilotSessionTray.App\       <- WPF tray host (MVVM, H.NotifyIcon.Wpf)
    CopilotSessionTray.Core\      <- polling/detection engine, models (no UI deps)
    CopilotSessionTray.Core.Tests\<- xUnit tests, fixture-based
  CopilotSessionTray.slnx
```

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| **State machine gets stuck** if `ISessionDetectionEngine` derives "session left Working" only from specific `events.jsonl` event types, and some real exit path (crash, an unusual autopilot completion, `session.shutdown` without a clean prior turn-end) never emits one — the exact bug found in GitHub's own official taskbar-presence feature (a session's taskbar card stuck spinning forever because the transition was only wired to `session.idle`, which some completion paths don't emit; see [github/copilot-cli#4771](https://github.com/github/copilot-cli/issues/4771)). | Never let `events.jsonl`-derived state be the sole authority for Working/Idle (see §3 "Signal authority"): re-derive it from `open-sessions-state.json`'s own `working`/`refreshedAt` fields + `IProcessLivenessChecker` on every poll, so a stuck/incomplete event-derived state self-corrects on the next cycle instead of persisting. Treat `events.jsonl` as detail/enrichment only. |
| On-disk formats are undocumented and may change across Copilot CLI versions | Defensive parsing (ignore unknown fields/event types), version-guard against `config.json` if it reports a version, log-and-skip instead of crashing. |
| `session-store.db` locked/busy while CLI writes (WAL) | Always open read-only; retry with backoff on `SQLITE_BUSY`; never treat this DB as the real-time signal (use it for history metadata only). |
| `FileSystemWatcher` misses rapid/atomic writes | Pair with a periodic fallback poll (~10s). |
| False "unread" from historical/old sessions on first run | On first launch, baseline all existing sessions as "read" (only notify on transitions observed *after* the app starts). |
| App itself accidentally corrupts or writes into `.copilot\*` | Hard rule: `Core` file/DB access is read-only; all app state lives under `%LOCALAPPDATA%\CopilotSessionTray\`. |
| Official GitHub taskbar-presence feature ships later and overlaps this app | Keep this app fully independent/uninstallable; revisit once that feature is enabled by default. |

### 9.1 Design-review findings (2026-09-29) — resolved, plus what's still deferred

A full pass over the shipped code (not just this plan) surfaced gaps that
existed in practice but were never written down anywhere. First recorded
here as open questions, then acted on the same day per "decide, don't ask"
— resolutions below; two real bugs found in the same original pass (a
hardcoded-stale tray menu label, and a non-deterministic lock-file pick in
`SessionLockFileInspector`) were fixed separately and aren't repeated here.
A third real bug, reported by the user on 2026-10-01, is also fixed
separately — see §9.2.

- **`ISessionLauncher`/`IYoloTaskRunner` were unused — resolved: retrofitted.**
  `Core.Services.SessionLauncher`/`YoloTaskRunner` are now real
  implementations; `TrayViewModel` calls through them instead of its old
  private `LaunchCopilotInTerminal` methods (removed). Both share a new
  internal `CopilotTerminalLauncher` helper for the actual `wt.exe`/script
  logic. `IYoloTaskRunner.StartInteractiveAsync` gained an additive
  `allowAllPermissions` parameter (default `true`) so it can express what
  the real "start new task" checkbox already did. `RunHeadlessAsync` is
  genuinely new (no prior code path): runs `copilot -p ... --allow-all
  --no-ask-user --output-format json` unattended and returns captured
  output — for later use investigating whether a long-running session
  looks genuinely stuck (§9's risk). Routed through `cmd.exe /c` rather
  than starting `copilot` directly: found empirically that
  `UseShellExecute=false` (required to redirect stdout) only assumes a
  `.exe` extension and won't resolve an npm-style `.cmd` shim the way a
  shell does.
- **Read-marker/rename persistence was built but not wired up — resolved:
  wired.** `MarkSessionRead`/`MarkAllRead`/`RemoveSession` now call
  `IAppStateStore.SaveReadMarkerAsync`; the summary panel's rename now
  calls `SetCustomDisplayNameAsync`. Gated on `IsShowingLiveData` so demo
  scenarios (fixed ids like `demo-1`) never persist and stay deterministic
  across cycles — only real (live/closed) sessions do. Dismissed sessions
  are now excluded when reloading the live scenario; custom names are
  re-applied on every load (both the live scenario and the history window).
  Verified end to end against real data (including cleanup) before landing.
  Assumption made and worth reconfirming: dismissal only hides a session
  from the live popup, not from the history window, which intentionally
  still shows everything.
- **Zero automated tests — resolved: 41 xUnit tests added.** Required
  making the file/db/registry paths in all Core services constructor-
  injectable (optional parameter, defaulting to the real path — every real
  call site unaffected) so tests use temp fixtures, never this developer's
  real `.copilot` folder. Covers `AppStateStore`, `OpenSessionsRegistryReader`,
  `SessionHistoryStore` (against a real-schema temp SQLite db),
  `SessionLockFileInspector` (including a regression test for the
  freshest-lock-file bug fix), and `CopilotTerminalLauncher`'s pure
  script-building/sanitization logic. Not yet covered: `ProcessLivenessChecker`,
  `SessionLauncher`/`YoloTaskRunner`'s actual process-launch paths (real OS
  process interaction — would need a process-starting abstraction to fake
  cleanly; deferred, same reasoning as `ProcessLivenessChecker` originally),
  and anything in the `App` project (ViewModels/windows).
- **`TrayViewModel` size — attempted, effectively a wash, still deferred.**
  Retrofitting `ISessionLauncher`/`IYoloTaskRunner` removed the ~90-line
  `LaunchCopilotInTerminal`/`SanitizeForCmdExe` methods, but the new
  persistence-wiring logic (marker/override helpers, dismissal filtering)
  added roughly the same amount back — net change negligible (777 → 771
  lines). Splitting it up remains a real, undecided question, better
  revisited once `ISessionDetectionEngine` work forces a bigger restructure
  anyway rather than done twice.
- **No DI container — resolved: `Microsoft.Extensions.DependencyInjection`
  added.** `TrayViewModel` now takes all 7 dependencies via constructor
  injection instead of field-initializer `new()` calls; `App.xaml.cs` is
  the composition root (`ConfigureServices`, all singletons — same
  one-instance-for-the-app's-lifetime behavior as before, just built in one
  place). `MainWindow` is also DI-resolved (also took a constructor
  parameter for `TrayViewModel`, since XAML can no longer `new()` it up
  without a parameterless constructor). Verified: DI-resolved
  `TrayViewModel`/`MainWindow` share the same singleton instance and
  correctly wired `DataContext`; demo and live scenarios (exercising all 7
  injected dependencies, including the persistence wiring from above) both
  still work identically; real compiled exe still starts/stops cleanly;
  all 41 existing tests unaffected (Core itself wasn't touched). This also
  now means `TrayViewModel` itself could be unit-tested with fake
  dependencies (constructor injection makes that a real seam for the first
  time) — not done yet, still a gap, but no longer a structural blocker.
- Two narrower, lower-priority notes, still just notes: a logical
  (non-crashing) race is possible if an async session-loading command and a
  synchronous one interleave mid-`await` on the UI thread; and
  `AppStateStore`'s in-process `SemaphoreSlim` doesn't itself protect
  `app-state.json` against two *process* instances writing at once — now
  moot in practice now that `App`'s single-instance `Mutex` (§9.3 follow-up)
  prevents a second process from ever getting far enough to try.

### 9.2 Bug fix (2026-10-01): "Resume with Summary" seeded a nonsense prompt

**Reported by the user**: resuming a real, closed session ("Architect",
`cwd=C:\Git`) via "Resume with Summary" seeded the new session with the
literal prompt *"Summary of prior work on Architect: C:\Git (last status:
Closed)."* — read by the user as a possible wrong/garbled working
directory.

**Root cause**: `SessionItemViewModel.Summary` always fabricated that
templated sentence from the display name/detail/status fields — it never
actually called `ISessionHistoryStore.GetCheckpointsAsync` despite
`ISessionLauncher.StartNewSessionFromSummaryAsync`'s own doc comment always
describing that as the design (a session's most recent real
`SessionCheckpoint.Overview`, which Copilot CLI already writes during a
session). This bug predates the 2026-09-29 retrofit pass — it was a Phase 1
demo placeholder that simply never got replaced once real session data was
wired up in Phase 2, so every "Resume with Summary" click for a *real*
session was silently seeding the new terminal with fabricated nonsense
instead of an actual summary. The `C:\Git` in the user's example was not
itself wrong (confirmed directly against the real `session-store.db`: that
session's `cwd` genuinely is `C:\Git`, since this whole project's CLI
sessions are invoked from that root) — it just looked wrong once mashed
into a fake sentence with no real content behind it.

**Fix**: added `TrayViewModel.GetResumeSummaryAsync(sessionId, fallback)`,
called from both `BuildOpenSessionItemAsync`/`BuildClosedSessionItemAsync`,
which resolves real text in priority order: (1) the session's most recent
checkpoint `Overview`, (2) its own title/summary field
(`SessionSummary.Summary`), (3) a plain, honest "No summary is available
for this session yet." message — never a fabricated sentence.
`SessionItemViewModel`'s constructor gained an optional `realSummary`
parameter; when provided (every real-session call site) it's used as-is,
and the old placeholder formula now only ever applies to the Phase 1 demo
rows (0-4), which still legitimately have no real data behind them.
Defensively truncates at 4000 characters (`TruncateForResumePrompt`) since
the fallback title/summary field can occasionally hold a session's entire
raw system prompt verbatim for some automated/scripted session types
(confirmed empirically against real data) — many KB of that embedded in
the generated launch script's `copilot -i "..."` line would silently
overflow cmd.exe's ~8191-character single-line limit.

**Verified**: against the real `session-store.db`, directly confirmed the
"Architect" session's real checkpoint overview is substantive, genuine
content (two checkpoints, 779/903 characters) — and reflectively invoked
the actual shipped `GetResumeSummaryAsync` method (not a reimplementation)
against it, confirming it now returns that real overview instead of the
old fabricated sentence. Also verified the fallback chain for a
nonexistent session id (no checkpoints: falls back to a provided title, or
the honest "no summary" message if nothing at all is available) and the
truncation guard (a 10,000-character input correctly truncated to ~4014
characters). All 64 existing xUnit tests still pass unaffected (this fix
only touches App-layer code with no dedicated unit tests yet, consistent
with the rest of `TrayViewModel`/`SessionItemViewModel`).

#### Follow-up (same day): the no-checkpoint fallback was itself too thin

**Reported by the user** (with a screenshot of the real Copilot CLI TUI):
after the fix above, resuming a session still produced a near-useless
prompt — a brand new session seeded with literally *"Summarize Architect
Work"*, causing the real agent to reasonably reply "there's no prior
context... I'm trying to figure out what the user is actually referring
to."

**Root cause, traced via direct inspection of the real `session-store.db`
and the resulting session's own real `events.jsonl`**: the user resumed
from a *different*, confusingly-similar-titled session than they intended.
The original "Architect" session (`51e349a0...`, 2 real checkpoints) was
last updated 2026-09-29, so it had scrolled out of the "10 most recent
closed sessions" list by now. A short, one-turn *derivative* session
(`dbf7671b...`, created earlier the same day — itself a byproduct of the
pre-fix bug above, title auto-generated as "Summarize Architect Work") was
more recent, so it appeared in that list instead, easily mistaken for the
original given the near-identical name. That derivative session has **zero
checkpoints**, so the (correctly working) fix above fell through to its
bare title field exactly as designed — but a 3-word auto-generated title
carries essentially no real content for a brand new, zero-context session
to act on. This is a real, general gap, not specific to this one session:
*any* short session that never reaches a checkpoint hits the same thin
fallback.

**Fix**: added a new middle tier to `GetResumeSummaryAsync`'s fallback
chain, between the checkpoint overview and the bare title —
`BuildTurnsFallbackSummary`, which pulls the session's actual
`ISessionHistoryStore.GetTurnsAsync` history (real stored
`SessionTurn.UserMessage`/`AssistantResponse` rows) and recaps "Originally
asked: ..." (first turn's user message) + "Most recent outcome: ..." (last
turn's assistant response). Only falls through to the bare title if there
are no turns either (a session that was opened but never actually
exchanged a message). Updated priority order is now: (1) latest checkpoint
overview, (2) turns-based recap, (3) bare title/summary field, (4) honest
"no summary available" message.

**Verified** against the real `dbf7671b` derivative session (the exact one
from the user's screenshot): confirmed it has 0 checkpoints and exactly 1
turn, and that the new turns-based recap now surfaces that turn's real
content instead of the bare title — strictly richer and directly sourced
from the session, even though in this *specific* degraded case the turn's
own content is itself a quote of the original bug (an inherent limit: no
fallback tier can manufacture context that was never captured in the first
place). Separately re-confirmed the original "Architect" session's 2 real
checkpoints are untouched and still take priority, so normal/common
sessions are unaffected by this change. All 69 existing xUnit tests
(Core-only — this fix is entirely in App-layer `TrayViewModel`, like the
fix above) still pass.

**Known remaining gap, not fixed here (noted, not acted on without a
decision)**: nothing currently prevents the "telephone game" of repeatedly
resuming-from-a-resume, nor visually distinguishes a derivative session
from its original in the history list — a user can still pick the wrong,
already-degraded entry by name alone. A real fix would need this app to
track provenance locally (e.g. "session X was created via Resume-with-
Summary, sourced from session Y") via `IAppStateStore`, since Core's
`.copilot` access is strictly read-only and Copilot CLI's own data has no
such concept. Left as a known gap rather than guessed at, since it's a
real design/UX choice (badge in the list? collapse/hide derivatives?
block chaining entirely?) rather than a one-line fix.

#### Follow-up (same day): the resumed session carried no trace of its origin

**Reported by the user**, with a screenshot of Copilot CLI's own real
`/resume` tip ("Switch sessions by local or cloud history ID, task ID, or
name") shown directly above a freshly-resumed-with-summary session: none
of those three things — id, task id, or name — were anywhere in the new
session's content. This is precisely the "known remaining gap" flagged
above, now reported directly by the user with concrete evidence rather
than a hypothetical.

**Fix**: every tier of `GetResumeSummaryAsync`'s fallback chain (checkpoint
overview, turns recap, bare title, "no summary" message) is now wrapped by
a new `BuildSummaryWithProvenanceHeader`, which prepends a short, always-
present header: the real session id — the exact same id this app's own
"Resume with history" action already passes to `copilot --resume=<id>`, so
it's confirmed resumable the same way via Copilot CLI's own `/resume <id>`
slash command too — plus the session's title if known, plus an explicit
`/resume <id>` hint, framed as background context rather than an
instruction, so the receiving agent reads it as metadata rather than
something to act on. Since `SessionItemViewModel.Summary` (this same text)
is also what the summary panel displays read-only, the id is now visible
there too, not just in the newly-seeded session.

Note this doesn't fully close the "known remaining gap" above — it makes
the *original* session's id discoverable and directly resumable from
within the derived session/summary panel (so a user who ends up on a
degraded derivative can now trace or jump back to the real original by
hand), but still doesn't *prevent* picking a degraded derivative in the
first place, or label it as one in the list. Left as a smaller, directly
actionable fix now; the bigger provenance-tracking question above remains
open.

**Verified**: defensively bounded the title portion of the new header to
80 characters separately from the body's own 4000-character truncation —
necessary because the same raw-system-prompt edge case that motivated the
body's truncation could otherwise sneak back in through this new "title"
path unbounded; confirmed via a reflective scratch harness against a
5000-character pathological title that the header's title segment never
contains the untruncated input and the total output stays comfortably
under cmd.exe's real ~8191-character limit. Also reflectively re-verified,
against the exact real sessions from both fixes above (the real
"Architect" checkpoint-bearing session, and the degraded `dbf7671b`
turns-only derivative), that the header now correctly shows each one's
real id/title while the underlying body content from the prior two fixes
is still intact and unchanged. All 69 existing tests pass (Core-only —
this fix, like the two before it, is entirely in App-layer
`TrayViewModel`). Verified the real compiled app — which was live and
running, serving the user, for both of today's prior fixes too — stays
responsive after being restarted with this change.

#### Follow-up (same day): make the live view the default, not a 6-click cycle

**Requested by the user**, alongside asking that the live view's session
names/ids be double-checked for correctness (re-verified as part of this
same change — see below; both call sites already used the real, full
session id and a real resolved name, no defect found there).

**Change**: `StartMonitoring()` (the one real call site, invoked once from
`App.xaml.cs`'s `OnStartup` right after the tray icon exists) now also
kicks off `LoadLiveSessionsScenarioAsync()` immediately, and
`_demoScenarioIndex` now starts at `5` (the live scenario's own index)
instead of its implicit default of `0`. The constructor itself still seeds
harmless demo scenario 0 as a brief placeholder (kept deliberately
side-effect-free, consistent with its existing documented design, for any
future unit test that constructs this ViewModel directly without going
through the real app startup path) — in the real running app this is
visible for well under a second before `StartMonitoring()` replaces it.
"Cycle demo data" needed **no changes at all**: it still walks the exact
same `0,1,2,3,4,5,0,...` sequence as always, just starting one step further
along, so every demo state (0-4) remains exercisable on demand exactly as
before, per the user's explicit ask to keep that working.

**Verified**: a scratch harness constructing the real `TrayViewModel`
exactly as `App.xaml.cs` does, then calling the real `StartMonitoring()`
and inspecting `Sessions`/`IsShowingLiveData` afterward, confirmed: (1) the
view immediately after startup is live (`IsShowingLiveData == true`,
`HeaderSubtitleText` shows the "🟢 LIVE" text, 11 real sessions loaded from
this machine's real `.copilot` data — none of them fake `demo-N` ids);
(2) every single loaded session's id parses as a real GUID (confirming
each one is the same kind of id already proven usable with
`copilot --resume=<id>`/`/resume <id>`, not a truncated or synthesized
value); (3) cycling 6 times in a row from this starting point produces the
expected `demo,demo,demo,demo,demo,LIVE` sequence, confirming the existing
cycle mechanism is completely undisturbed. All 69 tests still pass. The
real compiled app was stopped, rebuilt, and relaunched with this change —
confirmed responsive afterward.

### 9.3 Feature (2026-10-01): startup reliability — auto-launch with crash-recovery

**Requested by the user**: make sure the app is always running, and launch
it at Windows startup.

**Gap found**: a run-at-startup toggle already existed (§10 Phase 1), but
it only ever wrote a plain per-user Registry Run-key entry — which fires
once at logon and provides no recovery if the app later crashes mid-session.

**Fix**: `Services/StartupRegistration.cs` (`IsEnabled()`/`SetEnabled(bool)`
— same public surface, no caller changes needed in `TrayViewModel`) now
registers/removes a per-user **Scheduled Task** instead, built via an XML
task definition imported through `schtasks.exe /Create /XML` (consistent
with this app's existing pattern — see `CopilotTerminalLauncher` — of
shelling out to a built-in Windows executable rather than adding a Task
Scheduler NuGet dependency or raw COM interop). Key settings:
`LogonTrigger` (this user's own logon only) + `LogonType=InteractiveToken`/
`RunLevel=LeastPrivilege` (interactive desktop session, the WPF tray icon's
hard requirement — but still no admin elevation); `ExecutionTimeLimit=PT0S`
(no time limit — Task Scheduler's own default, commonly 72 hours, would
otherwise forcibly kill this intentionally-long-running tray app);
`RestartOnFailure` (1-minute interval, up to 999 times) — the actual
crash-recovery behavior this revision exists for, since Task Scheduler
only restarts on a non-zero/crash exit code and correctly leaves an
intentional "Quit" (clean exit, code 0) alone; `MultipleInstancesPolicy=
IgnoreNew` as a side-benefit (prevents Task Scheduler itself from
double-launching this one task at logon) — explicitly **not** a full fix
for the still-open single-instance-guard gap (§9.1), since it can't stop a
manually-started second copy running alongside it.

**Verified**: test-registered a throwaway task via the real XML shape and
confirmed empirically (`Get-ScheduledTask`) that `RestartCount`/
`RestartInterval`/`ExecutionTimeLimit`/`LogonType`/`RunLevel` all import
exactly as specified, and that registration succeeds under this user's own
standard (non-admin) token. Then reflectively invoked the actual shipped
`StartupRegistration.BuildTaskXml`/`SetEnabled`/`IsEnabled` methods (not a
reimplementation) against the real compiled `CopilotSessionTray.App.dll` —
confirming `IsEnabled()` correctly flips `false → true` after `SetEnabled(true)`
and the registered task's `Execute` path matches the real app exe exactly
(caught and corrected one harness mistake along the way: invoking
`SetEnabled` from a PowerShell host process captures *that* process's own
path via `Environment.ProcessPath`, not the target app's — resolved by
invoking `BuildTaskXml` directly with the real exe path instead, which is
also exactly what happens for real when the *actual* running app calls
`SetEnabled` on itself). Solution rebuilds clean (0 warnings/errors); all
69 existing tests still pass (this change is entirely new App-layer code
with no dedicated tests yet — consistent with the rest of the App project,
see §9.1). Real compiled app stopped, rebuilt, and relaunched — confirmed
responsive, and the Scheduled Task now correctly targets its exe path.

#### Follow-up (same day): single-instance guard, closing the gap this section itself flagged

`MultipleInstancesPolicy=IgnoreNew` above only stops the Scheduled Task
from double-launching at logon — it does nothing about a manually-started
second copy. Closed properly with a `System.Threading.Mutex`
(`"CopilotSessionTray-SingleInstance-Mutex"`, no `Global\` prefix needed
for a single-user tool) acquired first thing in `App.OnStartup`, before any
other startup work: if this process didn't create it (`createdNew ==
false`), it shows a plain "already running" `MessageBox` and calls
`Shutdown()` immediately, leaving the original instance completely
untouched. `OnExit` only calls `ReleaseMutex()` when this instance actually
owns it (`_isPrimaryInstance`) — the duplicate's own early-exit path also
flows through `OnExit` holding a handle to the same named mutex, but never
owned it, so unconditionally releasing would throw
`SynchronizationLockException`.

**Verified**: launched a real primary instance, then a real second one
alongside it — confirmed the second showed the expected "Copilot Session
Tray" dialog (via `MainWindowTitle`) while the first stayed fully
responsive and untouched throughout (same PID, same start time); closing
the dialog made the duplicate process exit cleanly with no effect on the
original. Solution rebuilds clean; all 69 tests still pass.

### 9.4 Feature (2026-10-02): real artwork for the tray icon, replacing the flat color square

**Requested by the user**, who supplied original SVG art: a minimal,
multi-armed bodhisattva seated in lotus posture — along with the question
of whether SVG was even the right format to provide.

**Answered first, then implemented the recommended path**: SVG is the
right *source* format, but Win32 tray icons are raster, and `TaskbarIcon`'s
`GeneratedIconSource` (the control already in use — see §10 Phase 1) draws
everything itself via GDI+, with no SVG support at all. Investigated the
control's actual rendering code directly
(`GeneratedIconSource.System.Drawing.cs`/`SystemDrawingIconGenerator.Generate`,
fetched from the real H.NotifyIcon source, not assumed) before writing any
code: confirmed `BackgroundSource` (an `ImageSource`, previously unused in
this app) supplies the base bitmap, but the control still fills its own
`Background` shape **on top of** that image afterwards — so simply setting
both would hide the artwork under the existing flat-color fill. Fixed by
setting `Background="Transparent"` in `MainWindow.xaml` and binding the new
`BackgroundSource` to a new `TrayViewModel.IconBackgroundImageSource`.

**Asset pipeline**: `cairosvg` (the obvious Python choice) turned out to
need a native `libcairo` binary not present on Windows and not pip-
installable there — rather than fight that, switched to `SkiaSharp`/
`Svg.Skia` (a throwaway console tool, not a shipped dependency — Core/App
still reference nothing new). Three PNGs are pre-baked from the original
SVG (kept at `Assets/tray-icon-source.svg` for provenance, excluded from
the build), one per existing state color (`TrayIcon.{Working,Unread,Idle}.png`
— `#3CB371`/`#FF4500`/`#808080`, exactly `IconBrush`'s old colors, now
retired): `currentColor` swapped to white, rendered via `SKSvg`, composited
onto an anti-aliased solid-color circle. Loaded once into static,
`Freeze()`-d `BitmapImage` fields (bundled app resources, never changing at
runtime) via `pack://application:,,,/Assets/...` URIs; `.csproj` gained an
explicit `<Resource Include="Assets\TrayIcon.*.png">` (the `.svg` source is
deliberately not globbed in).

**The live unread-count/dot badge needed rework, not just a recolor**:
first attempt kept the existing dead-center text positioning and only
tried nudging it to a plain top-right corner. Rendering the *real* control
(`GeneratedIconSource.ToBitmap()`, via a throwaway WPF console harness
referencing the same H.NotifyIcon package — not a mockup) and visually
inspecting the output showed the bodhisattva's own outstretched arms reach
much further into the icon's corners than expected, directly colliding
with the badge text. Fixed properly, matching the universal "notification
count" convention (Android/Teams/Slack-style): baked a small, fixed, solid
circle backdrop (`#D32F2F`, a fixed accent independent of state color) into
the bottom-right corner of the Working/Unread PNGs only — confirmed
`IconGlyph` is *only* ever empty when the background is Idle/gray, so Idle
never needs one. `IconFontSize`'s base size dropped from 44 to 26 (44 was
sized for the old full-icon centering, too large for the small backdrop),
and `IconTextMargin` now centers the measured glyph inside that backdrop
circle's exact baked box instead of the whole canvas — the two had to be
kept in sync by hand (documented in both places) since nothing enforces
that automatically.

**Verified**: solution rebuilds clean (0 warnings/errors); all 69 existing
tests still pass (this change touches only App-layer code and bundled
assets, no `Core` changes). Visual correctness was checked by rendering
the actual shipped `GeneratedIconSource` control (not a reimplementation)
for all 3 background states crossed with representative glyphs (none,
"●", "3", "9+") and inspecting the output images directly — caught and
fixed the arm-collision problem this way before it ever reached the real
app. Separately downsampled the final art to real tray sizes (16/20/24/32px)
to confirm the figure and state color stay legible that small; confirmed
the exact unread count is never actually lost even where digits blur at
16px, since `ToolTipText` already surfaces it on hover regardless. Real
compiled app stopped, rebuilt, and relaunched — confirmed responsive with
no visible error dialogs (`EnumWindows`-checked, not just "didn't crash").

#### Follow-up (same day): the same artwork as a faint watermark in the popup

**Requested by the user** (with a screenshot of the popup's empty state):
add the same SVG as a semi-transparent gray background, "just to increase
the perception of the logo."

**Implemented**: a 4th baked asset, `TrayIcon.Watermark.png` — the glyph
alone (no circle/badge), recolored gray (`#808080`, matching the Idle tray
color), transparent surroundings. Added to `MainWindow.xaml` as a plain
`Image` (`Opacity="0.08"`, `220x220`, centered, `IsHitTestVisible="False"`)
declared *before* (so behind, in z-order) the `ListBox`/empty-state
`TextBlock`, both of which already keep a transparent background — so it
shows through both the big empty state and, more faintly, behind real
session rows.

**Verified visually, not just "it builds"**: rendering a full `MainWindow`
needed its own small fixes, each confirming something real rather than
assumed. (1) `Window.Measure()` throws a WPF internal invariant failure
without a real native HWND — worked around by rendering `window.Content`
(the root `Grid`, a plain `FrameworkElement`) directly instead, which
needs no HWND. (2) A bare console harness never registers the `pack://`
URI scheme the way a real `Application` does as a side effect of its own
static init — `TrayViewModel.LoadIconImage`'s pack URIs threw
`UriFormatException` until the harness constructed a (never-`Run()`)
`System.Windows.Application` first. (3) `pack://application:,,,/` resolves
against the *entry assembly* — correctly `CopilotSessionTray.App.exe`
itself in the real app (already proven working), but the harness's own
`.exe` in this throwaway project, so it needed its own local copy of the
same `Assets/*.png` files purely to resolve identically; no real app code
changed for this. With all three resolved, rendered the actual
`TrayViewModel`/`MainWindow` (cycling, with the real
`CycleDemoScenarioCommand`, to demo scenario 3 — the genuine "Empty" case,
matching the user's screenshot exactly) to a PNG via
`RenderTargetBitmap` and inspected it directly: confirmed the watermark
reads clearly as the bodhisattva figure, sits centered behind "No sessions
(demo)" without competing with it, and — checked separately on scenario 0
— stays unobtrusive behind real populated rows too, mostly hidden but
peeking through faintly where rows don't fully cover it. Solution rebuilds
clean; all 69 tests still pass (App-layer/asset-only change, no `Core`
changes). Real compiled app stopped, rebuilt, and relaunched — confirmed
responsive.

**Same-day micro-follow-up**: the user asked to "bring it up a little"
after seeing it live (screenshot showed the figure sitting low, pedestal
close to the footer buttons). Nudged with `Margin="0,0,0,40"` alongside
the existing `VerticalAlignment="Center"` — an asymmetric bottom-only
margin shrinks the centering box from the bottom edge only, pulling the
visual center upward by half the margin, a cleaner idiom than a negative
top margin. Re-rendered the same way (real `MainWindow`, scenario 3) to
confirm the new position before touching the live app; same verification,
same result (69 tests, clean rebuild, responsive restart).

### 9.5 Real crash (2026-10-02): §9.4's tray-icon artwork had to be reverted

**Reported by the user**, with a screenshot: "it crashed and I can't shut
down, it keeps popping up these" — a stack of repeated "Copilot Session
Tray — unexpected error" dialogs, the front one showing a
`System.OutOfMemoryException` deep in WPF's own composition engine
(`DUCE.Channel.SyncFlush`/`HwndTarget.UpdateWindowSettings`), a second
dialog behind it showing fragments of a different exception mentioning
`H.NotifyIcon`/`imageSource`/`cancellation`.

**Root cause, found via Windows Event Viewer, not guessed at**: an
`Application`-log `.NET Runtime` entry (from 2026-09-29 — the day the
background monitor first shipped, i.e. a pre-existing risk, not something
§9.4 introduced from nothing) captured the exact second dialog's exception
in full:
```
System.Runtime.InteropServices.ExternalException (0x80004005): A generic error occurred in GDI+.
   at H.NotifyIcon.GeneratedIconSource.ToIconAsync(CancellationToken cancellationToken)
   at H.NotifyIcon.ImageExtensions.ToIconAsync(ImageSource imageSource, CancellationToken cancellationToken)
   at H.NotifyIcon.TaskbarIcon.<>c__DisplayClass178_0.<<OnIconSourceChanged>g__OnGeneratedIconSourceOnDependencyPropertyChanged|0>d.MoveNext()
```
Cross-referenced against `GeneratedIconSource`'s own real source
(`GeneratedIconSource.cs`): its `Refresh()` method is
`OnChanged(); _ = OnDependencyPropertyChanged();` — an explicitly
un-awaited, fire-and-forget async call. `TaskbarIcon` runs this for
*every single* `GeneratedIconSource` dependency-property change
independently. §9.4's `RecomputeAggregateState()` changes `Text`,
`TextMargin`, and (newly, in §9.4) `BackgroundSource` together on every
real state transition — three near-simultaneous, unawaited async icon
regenerations, each touching the same non-thread-safe GDI+ `Bitmap`/
`Graphics` objects with no synchronization between them. "A generic error
in GDI+" is the textbook symptom of exactly that kind of concurrent
access. This race has apparently always existed (confirmed from a log
entry predating today), but a flat `SolidColorBrush` fill completes near-
instantly, keeping the overlap window too narrow to matter in practice;
`BackgroundSource`'s extra bitmap-decode work (introduced in §9.4) widened
that window enough to hit it reliably within minutes of continuous 3-
second polling — turning a dormant, theoretical risk into a real,
user-facing crash loop. Separately, `DispatcherUnhandledException`
(added 2026-09-29 specifically so failures are visible instead of silent)
turned out to make this *specific* failure mode worse, not better: each
recurrence is "handled" and the process survives, so the same underlying
problem fires again moments later, popping up another dialog faster than
a user can dismiss them — explaining "I can't shut down" precisely (the
process never actually exits; Task Manager / `Stop-Process -Force` was
needed).

**Fix, two parts**:
1. **Reverted §9.4's `BackgroundSource` entirely** — `TrayViewModel.IconBrush`
   is back to a flat `SolidColorBrush` (exactly as before §9.4), and
   `IconFontSize`/`IconTextMargin` are back to their original full-canvas
   centering. This removes the specific trigger that turned a rare race
   into a frequent one. The artwork itself isn't lost: it now lives only
   in the popup's watermark (§9.4), which is a plain WPF `Image.Source`
   binding — no `H.NotifyIcon`/`GeneratedIconSource` involvement at all, so
   none of this risk applies there, however often it's rebound.
2. **`App.OnDispatcherUnhandledException` now rate-limits itself**: more
   than `MaxConsecutiveExceptionDialogs` (3) dialogs within a tight
   recurrence window (5s) stops showing any more and lets the process
   terminate for real instead of suppressing-and-repeating forever.
   Deliberately not a graceful `Shutdown()` for that final case — whatever
   caused a tight failure loop may have already left the dispatcher/UI in
   a bad state, so finishing via the normal unhandled-exception path is
   safer than risking another broken cycle. This is a structural
   safety net independent of today's specific bug: *any* future recurring
   failure, whatever its cause, can no longer trap the user in an
   unstoppable dialog storm.

**Also addressed in the same pass**: the user's separate ask — color the
popup watermark to match the tray icon's current state color — implemented
via 3 new baked variants (`TrayIcon.Watermark.{Working,Unread,Idle}.png`,
green/orange/gray, figure only, no circle/badge) and a new
`TrayViewModel.WatermarkImageSource` selecting between them with the same
state priority as `IconBrush`. Confirmed deliberately safe to make this
live/reactive (unlike the tray icon): it's the same plain `Image.Source`
binding path as before, untouched by the bug above.

**Verified**: immediately killed the running process and disabled the
Scheduled Task (so `RestartOnFailure` couldn't relaunch it mid-
investigation) before diagnosing. After the fix, ran a real 5-minute soak
test against the actual compiled app (not a short smoke test like
previous verifications) — sampling memory/handle count every 30 seconds
through ~100 real 3-second poll ticks: memory stayed flat (210–214MB, no
growth trend) and handles *decreased* slightly (567→555) rather than
climbing, `Responding` stayed `True` throughout, zero visible windows
(`EnumWindows`-checked — no error dialogs), and zero new `.NET Runtime`
crash events logged for the entire window. Re-enabled the Scheduled Task
only after this passed. All 69 tests still pass; solution rebuilds clean.
Re-rendered the real `MainWindow` (same technique as §9.4) in the
"needs attention" demo scenario to confirm the watermark now genuinely
shows orange/red, matching the unread dots next to it.

**Lesson for next time, written down since it cost real user impact to
learn**: a short (seconds-to-low-minutes) manual verification run is not
enough to catch a race condition or slow resource issue — this exact
flaw passed §9.4's own "ran the real compiled app... stayed responsive"
check. A multi-minute soak test under continuous realistic polling is now
the bar for any change to the continuously-running background monitor or
anything it touches on every tick (the tray icon above all, given this
finding).

### 9.6 Palette unification, priority rework, flashing, and dead-code cleanup (2026-10-05)

**Requested by the user**, directly building on advice given the same session
(summarized back to them first, unprompted): unify "Working" to green
everywhere (icon *and* session-list text, not just the dot), flash the
tray icon gold for `WaitingForInput`, let `WaitingForInput`/unread win over
`Working` in the tray icon's priority, and remove the dead
`TrayAggregateState` enum.

**Color/text unification**: `SessionItemViewModel.StatusBrush`'s `Working`
case changed from `Brushes.DodgerBlue` to `Brushes.MediumSeaGreen` (now
identical to the tray icon's own green). `MainWindow.xaml`'s per-row
status/elapsed `TextBlock` — previously a fixed `Foreground="#999999"`
regardless of status — now binds `Foreground="{Binding StatusBrush}"`, so
the whole "Working · 3m" line (not just its dot) renders in the matching
color for every status, not only `Working`.

**`TrayAggregateState` is no longer dead code**: it already existed
(`NoSessions`/`Idle`/`Working`/`AttentionNeeded`) but was computed every
poll and never actually read by anything — `IconBrush`/`IconGlyph`/
`WatermarkImageSource` each independently reimplemented their own
`IsAnyWorking`/`UnreadCount` checks. Added a `WaitingForInput` case and
made it the one real source of truth: `IconBrush`/`WatermarkImageSource`
are now plain switches over `AggregateState`, computed once in
`RecomputeAggregateState`. New priority (highest wins — reversed from the
original, where `Working` always won): `WaitingForInput` and
`AttentionNeeded` ("this needs you") now both outrank `Working`
("something's just running"). A new `IsAnyWaitingForInput` property
(`Sessions.Any(s => s.Status == SessionStatus.WaitingForInput)`) mirrors
the existing `IsAnyWorking`. `ToolTipText` also gained a "waiting for
input" count alongside "working"/"need attention", for consistency.

**The flash feature needed a real design change after a second real
crash, not just an implementation**: a first attempt toggled
`IconBrush` (Goldenrod/Gray) continuously every 800ms for as long as
`WaitingForInput` held, via a dedicated `DispatcherTimer` — the obvious,
literal reading of "flash". A dedicated soak test (learned the hard way
in §9.5 to always soak-test anything touching the tray icon on a tight
interval) reproduced **the exact same H.NotifyIcon GDI+ race from §9.5**
within ~60–90 seconds — this time via flat-color toggling, with no image
involved at all, proving the bug is about *regeneration frequency*, not
specifically about image decoding being slow. §9.5's fix (revert to flat
colors) only ever made the *existing* 3-second-poll-driven, change-only-
on-real-transition pattern safe — it never made *arbitrary* high-frequency
`GeneratedIconSource` property churn safe in general, and continuous
800ms flashing is exactly that.

**Fix: a bounded flash burst, not continuous flashing.** On the actual
transition *into* `WaitingForInput` (not on every poll while already in
it), the icon flashes `FlashToggleCount` (6) times — about 4.8 seconds —
then the timer stops itself and the icon settles on a steady Goldenrod
for as long as the state persists, however long that is. This stays
within the same "occasional short burst of property changes, then quiet"
shape already proven safe (a normal state transition changes 3+
properties together, same order of magnitude as 6 bounded toggles),
rather than introducing a new "sustained high-frequency stream" shape
(the one pattern now proven, twice, to be unsafe with this library). If
the state leaves `WaitingForInput` mid-burst, the timer stops early and
the icon settles on its new (non-flashing) color immediately. The popup
watermark does *not* flash even for `WaitingForInput` (a 4th baked
Goldenrod variant, `TrayIcon.Watermark.WaitingForInput.png`, is static) —
it's a plain `Image.Source` binding with no H.NotifyIcon involvement, so
nothing requires it to stay bounded, but there's no equivalent "catch the
most urgent signal" reason to animate a background watermark either.

**Verified, at the same bar §9.5 set**: a dedicated 5-minute soak test of
the *first* (continuous-flash) design reproduced the crash directly
(confirmed via the real exception, not inferred) — proving the caution
was warranted, not theoretical. The *bounded-burst* redesign was then
soak-tested the same way: 5 full minutes with a real `TaskbarIcon`/
`GeneratedIconSource` (via `MainWindow.InitializeTrayIcon()`, matching
`App.xaml.cs` exactly) forced into `WaitingForInput`, sampling memory/
handles every 30s — flat memory (~95MB, no growth), handles *decreasing*
slightly, zero exceptions, and the burst visibly stopped after ~5s and
stayed steady for the remaining ~4.9 minutes. Separately verified
*correctness* (not just safety): transitioning into `WaitingForInput`
triggers exactly 6 toggles then settles; leaving it mid-state immediately
stops and shows the new color with no stray toggling; transitioning back
in later re-triggers a fresh 6-toggle burst. Re-rendered the real
`MainWindow` to confirm visually: the "Working" row's dot *and* text are
green, the `WaitingForInput` row's text is gold (matching its dot, as it
already did), and the popup watermark shows gold whenever any session is
`WaitingForInput`. All 69 tests still pass; solution rebuilds clean. Real
compiled app stopped, rebuilt, and relaunched — confirmed responsive after
60+ seconds against this machine's real live data, with zero visible
error dialogs and zero new crash-log entries.

#### Follow-up (same day): "Waiting for input" was the wrong label for what it means in practice

**Reported by the user**, with a screenshot of two real (not demo) sessions
genuinely idle for 5h11m and 1d16h, both labeled "Waiting for input" —
followed by a back-and-forth that corrected an assumption made earlier the
same day: the label's own wording implies "Copilot asked something and is
waiting on your immediate next message, mid-conversation" (and that's what
this assistant initially, incorrectly, told the user it meant). The
user's real evidence said otherwise: sessions idle for hours/a day are, in
their actual experience, simply *done* — not sitting mid-conversation
awaiting a quick reply.

**Root cause, confirmed against this plan's own §2.3 research rather than
re-guessed**: `open-sessions-state.json`'s `working` flag (the only signal
`SessionStatus.WaitingForInput` is derived from — see
`TrayViewModel.BuildOpenSessionItemAsync`) is just "is a turn currently
processing, yes/no" — Copilot CLI itself has no distinct signal for "this
genuinely still expects you to say something very soon" versus "this
finished and has been sitting untouched." Both report `working: false`
identically. For a session discovered already-idle (which is what
`WaitingForInput` specifically represents — see its own doc comment), the
long-idle case is overwhelmingly the realistic one, exactly as the user's
own two real sessions showed.

**Fix — wording only, not behavior**: `SessionItemViewModel.StatusLabelFor`
now displays `SessionStatus.WaitingForInput` as **"Completed"**, not
"Waiting for input". `TrayViewModel.ToolTipText`'s matching count changed
from "N waiting for input" to "N completed" for the same reason. The
underlying `SessionStatus.WaitingForInput` enum value, the tray icon's
gold flash trigger, and the §9.6 priority ordering are all **unchanged** —
the *behavior* the user asked for (flash gold, outrank Working) was
already correct for what they actually wanted; only the displayed English
description was wrong. Demo scenario 1's illustrative detail text
("Waiting on next instruction.") was also updated ("Deployed the hotfix;
smoke tests passed.") so the demo row's own fake detail text doesn't
contradict its new "Completed" label. The toast notification body shown
on a live `Finished` transition ("Finished a turn and is waiting for
input.") was deliberately left unchanged — that text describes a
different, genuinely-just-now event, not this long-idle steady state, so
it wasn't actually wrong.

**Verified**: this is a label/string-only change (no icon-regeneration
code touched), so no soak test was needed — confirmed instead by
rendering the real `MainWindow`/`TrayViewModel` (scenario 1) and reading
the real `ToolTipText` property: the row now reads "Completed · 9m" in
gold, and the tooltip reads "... completed ...". All 69 tests pass;
solution rebuilds clean. Real compiled app stopped, rebuilt, and
relaunched — confirmed responsive.

#### Follow-up (same day, again): "Completed" still overclaimed — demoted to a stub, not just relabeled

**Challenged by the user**, with sharp, correct reasoning: "if it was
already done by the time you looked, shouldn't it be idle and gray?" — if
this app genuinely can't tell *when* a `WaitingForInput` session actually
went idle (seconds ago vs. days ago), confidently labeling it "Completed"
overclaims something it doesn't know, same as "Waiting for input" did
before it. Gold, flashing, and outranking `Working` all imply "this is
fresh, important news" — which isn't a claim this app can actually back
for this particular status.

**Decision, given by the user directly rather than inferred**: rather
than silently demoting the status to a bare "Idle" and quietly dropping
the whole concept, mark the *distinction* ("is this genuinely awaiting a
reply, or just long-done?") as **not implemented** — keep the enum values
and the switch-case "slots" that would drive its visual treatment, but
stop pretending to compute something this app has no real signal for.
This preserves a clear, named place to plug in a real implementation
later (e.g. if a future Copilot CLI version exposes a genuine "awaiting
reply" flag or a specific `events.jsonl` terminal event), without losing
today's design work or having to rediscover where it plugs in.

**Implemented**:
- `TrayViewModel.IconBrush`/`WatermarkImageSource`: the `WaitingForInput`
  switch arms are removed; that status now falls through to the same
  gray/idle treatment as everything else below `Working`. Both properties'
  doc comments now explain this is a deliberate stub, not an oversight.
- `RecomputeAggregateState`: no longer ever assigns
  `TrayAggregateState.WaitingForInput` — priority reverts to
  `NoSessions > AttentionNeeded (unread) > Working > Idle`, the same
  structure as before the flash feature existed, keeping only the
  (separately well-justified, unchanged) `AttentionNeeded`-over-`Working`
  reversal from earlier today.
- **The entire flash-burst mechanism is deleted**: `FlashInterval`,
  `FlashToggleCount`, `_flashTimer`, `_isFlashOn`, `_remainingFlashToggles`,
  and the burst-start/stop logic in `RecomputeAggregateState` are all
  gone. This is also, concretely, the removal of the single riskiest piece
  of code added today — the one that needed a dedicated soak test and
  came with several paragraphs of crash-safety reasoning. Removing it
  because it's no longer wanted is a strictly good simplification
  regardless of the crash history.
- `SessionItemViewModel.StatusBrush`: `WaitingForInput` → `Brushes.Gray`
  (was `Goldenrod`). `StatusLabelFor`: `WaitingForInput` → `"Idle"` (was
  `"Completed"`) — "Idle" makes no claim either way, which is the honest
  answer.
- `TrayAggregateState.WaitingForInput` and `SessionStatus.WaitingForInput`
  (Core) are both kept, now documented as intentionally-unimplemented
  stubs rather than live, computed states.
- `TrayViewModel.ToolTipText`'s "N completed" segment is removed entirely
  (reverted to its pre-today wording) — there's no longer a confident
  claim to report a count of.
- The 4th watermark PNG (`TrayIcon.Watermark.WaitingForInput.png`, gold)
  and its `Resource` entry are deliberately **kept**, just not currently
  selected by any switch arm — ready to reuse instantly if this is
  properly implemented later, at zero cost today beyond its own bytes.

**Verified**: rebuilt clean (0 warnings/errors) — the clean rebuild itself
confirms no dangling references to the deleted flash-timer fields/
constants were missed. All 69 tests still pass. Reflectively exercised
the real `TrayViewModel` (scenario 1, the pure-`WaitingForInput` demo row)
and confirmed `AggregateState` is now `Idle` (not `WaitingForInput`) and
`IconBrush` is `#FF808080` (gray, not gold) — then rendered the real
`MainWindow` and confirmed visually: "Idle · 9m" in neutral gray, with the
gray/idle watermark. No soak test needed this time — the change is a
removal of risky code plus label/color simplification, not a new
continuously-ticking mechanism. Real compiled app stopped, rebuilt, and
relaunched — confirmed responsive.

#### Follow-up (same day, again): "When I clicked the cycle demo data, it seems the status are not synced" — actually a recurrence of the GDI+ race, reachable via ordinary clicks

**Reported by the user** in exactly those words. Investigated first at the
ViewModel level: subscribed to `TrayViewModel.PropertyChanged` and cycled
through all 6 demo scenarios directly — `AggregateState`, `IconBrush`, and
`Sessions` were perfectly consistent with each other at every step, so the
"desync" wasn't a ViewModel logic bug. Suspected the native tray-icon
rendering layer instead and built a rapid-fire stress test: 12 back-to-back
`CycleDemoScenarioCommand.Execute(null)` calls, no delay, against a **real**
`TaskbarIcon` (via `window.InitializeTrayIcon()`, not a mock) — and it
reproduced the exact same `ExternalException (0x80004005): A generic error
occurred in GDI+` crash from §9.5/§9.6, on demand, from nothing more than
ordinary fast clicking. No custom timer involved this time at all.

**This was a more severe finding than §9.5's original fix accounted for**,
on two counts:
1. It proved the earlier fix (removing the flash timer) only removed
   *one* trigger of the race, not the race itself — the race lives in
   `TaskbarIcon.IconSource`'s own binding plumbing, not in anything
   app-specific like a timer.
2. The stack trace showed these exceptions land on background
   `ThreadPool` threads (`System.Threading.ThreadPoolWorkQueue.Dispatch()`),
   **not** the UI dispatcher thread — meaning `App.OnDispatcherUnhandledException`
   (the dialog-storm fix from §9.5) cannot catch them at all. An unhandled
   exception on an arbitrary background thread terminates the whole process
   immediately by .NET default, with zero dialog — silently worse than the
   "can't shut down, keeps popping up dialogs" crash reported earlier.

**Root cause, confirmed by fetching H.NotifyIcon's real source from
GitHub, not guessed from behavior alone**: binding `TaskbarIcon.IconSource`
to a `GeneratedIconSource` makes `TaskbarIcon` subscribe to that source's
`DependencyPropertyChanged` event with an `async void` handler that calls
`Icon = await newValue.ToIconAsync()` — un-awaited by the caller and never
cancelled against any previous in-flight call. Two property changes
(`Text`, `Background`, `TextMargin`, ...) close enough together in time
race two concurrent `ToIconAsync()` calls against the same non-thread-safe
GDI+ `Bitmap`/`Graphics` objects. This is a defect in how `TaskbarIcon`
itself drives `IconSource`, not in anything specific to this app's demo
timer or its flash feature — both of those only mattered as *ways to
trigger it faster*.

**Fix — architectural, not another mitigation**: stop using
`TaskbarIcon.IconSource` entirely.
- `MainWindow.xaml`: the `<tb:TaskbarIcon.IconSource><tb:GeneratedIconSource
  .../></tb:TaskbarIcon.IconSource>` binding block is removed outright
  (replaced with an explanatory comment) — not merely left unused.
- `MainWindow.xaml.cs`: added a single reused `GeneratedIconSource
  _iconGenerator` field, and a new `UpdateTrayIcon()` method that copies
  `IconGlyph`/`IconBrush`/`IconFontSize`/`IconTextMargin` from the
  ViewModel onto it and assigns `TrayIcon.Icon = _iconGenerator.ToIcon()`
  — the library's *synchronous* bake method (confirmed via source:
  `public Icon ToIcon() { using var bitmap = GenerateIconBitmap(); return
  Icon.FromHandle(bitmap.GetHicon()); }`), called directly, not the async
  one. A new `OnViewModelPropertyChangedForTrayIcon` handler, subscribed
  once in the constructor, filters `TrayViewModel.PropertyChanged` for
  exactly those four properties and calls `UpdateTrayIcon()` synchronously
  on the UI thread — the same thread `TrayViewModel`'s own property
  setters already raise `PropertyChanged` on. `InitializeTrayIcon()` now
  also calls `UpdateTrayIcon()` once up front, since nothing will "change"
  to trigger the first render now that there's no reactive binding.

  Why this is actually race-free rather than just differently risky:
  nothing in this path is `async`/fire-and-forget, so a second call can
  only ever begin after the first has fully returned — overlapping calls
  are structurally impossible, not just statistically unlikely the way a
  faster machine or a removed timer merely made *less probable*.

**Verified empirically, not just reasoned about**:
- Solution rebuilds clean, 0 warnings/errors; all 69 tests still pass
  (Core untouched).
- Re-ran the **exact same rapid-fire stress test** against the new code: 11
  back-to-back calls (deliberately not a multiple of 6, so the result
  actually exercises a different scenario rather than coincidentally
  landing back on the same one), then 5 more rounds of 12 back-to-back
  calls each — 71 rapid-fire cycles total against a real `TaskbarIcon`,
  zero delay between calls. **Zero exceptions**, where the old code crashed
  almost immediately under the same conditions. `AggregateState=Working`
  correctly paired with `IconBrush=#FF3CB371` (green) throughout, confirming
  state/color consistency, not just crash-safety.
- Visual regression check: rendered all 6 demo scenarios' icons via an
  independent `GeneratedIconSource` (mirroring `UpdateTrayIcon()`'s own
  property mapping) to PNG and inspected them — green circle for Working,
  orange circle with unread-count badge for AttentionNeeded, gray circle
  for Idle, all rendering correctly with no corruption or blank output.
  The bake logic itself (`GenerateIconBitmap()`/`ToIcon()`) is unchanged
  library code; only the call site's sync/async timing changed, so this
  was a confirmation check rather than an expected source of new bugs.
- Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

#### Follow-up (same day, yet again): tray icon still didn't match the popup after the above fix — a second, independent bug

**Reported by the user**, with three screenshots: tray icon gray while the
popup showed a session "Working" (green); tray icon green while the popup
showed every session "Idle" (gray); tray icon red/orange while the popup
showed "Idle" (gray). In every case the tray icon's color matched the
*previous* state, not the current one — and the user also asked, fairly,
whether they just needed to restart the app to pick up the earlier fix (a
fresh restart had already happened before these screenshots, so this was
a real remaining bug, not a stale-binary question).

**Root cause, confirmed by direct reproduction rather than inferred from
the screenshots alone**: `RecomputeAggregateState()` raised its explicit
`OnPropertyChanged(nameof(IconBrush))` (and `IconGlyph`/
`WatermarkImageSource`/`IconTextMargin`/`ToolTipText`) calls **before** the
line that actually assigns the new `AggregateState` value. Since
`PropertyChanged` is an ordinary synchronous .NET event, any subscriber
that reads `IconBrush`'s getter *while handling* that notification —
including today's earlier fix, `MainWindow`'s own
`OnViewModelPropertyChangedForTrayIcon` → `UpdateTrayIcon()`, which does
exactly this — would evaluate `IconBrush`'s `AggregateState switch { ... }`
against the **old** `AggregateState`, one transition behind, every single
time. Built a small harness subscribing to `TrayViewModel.PropertyChanged`
and comparing `AggregateState` (read synchronously inside the `IconBrush`
notification handler) against what `Sessions` actually implied at that
instant: **7 of 8** rapid demo-cycle notifications showed a mismatch
before this fix. This is a second, independent bug from §9.6's `IconSource`
race — that one was about *overlapping* icon bakes; this one is a plain
ordering mistake that makes every single update exactly one step stale,
deterministically, with or without any concurrency involved at all. It
predates today's `IconSource` removal (the old reactive XAML binding would
have shown the exact same staleness, for the same reason — it just hadn't
been specifically noticed/attributed before).

**Fix**: reordered `RecomputeAggregateState()` so the `AggregateState =
...` assignment happens **first**, and all the explicit
`OnPropertyChanged` notifications for its dependent properties
(`UnreadCount`, `IsAnyWorking`, `IsAnyWaitingForInput`, `IconGlyph`,
`IconBrush`, `WatermarkImageSource`, `IconTextMargin`, `ToolTipText`) are
raised **after** — so by the time any subscriber's handler runs, reading
any of these properties reflects the new state. No subscriber or binding
needed to change; this was purely an internal ordering bug in one method.

**Verified**:
- Re-ran the same before/after harness against the fix: **0 of 8**
  mismatches (was 7 of 8).
- Combined with a repeat of the rapid-fire `TaskbarIcon` stress test from
  the previous follow-up (96 rapid-fire clicks total this time, across 8
  rounds of 12), now also asserting `AggregateState` matches what
  `Sessions` implies after *every single* click, not just checked at the
  end: **0 mismatches, 0 exceptions** — confirming this fix and the
  `IconSource`-removal fix compose correctly together (neither one
  reintroduces the other's bug).
- All 69 tests still pass; solution rebuilds clean (0 warnings/errors).
- Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

#### Follow-up (same day): "Read" didn't clear the red text, and a golden Guanyin glyph replaces the Idle dot

**Reported by the user**, two requests together: (1) "when I click read button, the color of the text is still red"; (2) "when it's idle, instead of using gray dot, can you use the guanyin svg in a golden color?"

**Issue 1 root cause**: `SessionItemViewModel.StatusBrush` switched only on `Status`, not `IsUnread` — `Finished` always rendered `OrangeRed`, forever, even after `TrayViewModel.MarkSessionRead` (the "✓ Read" button's command) flipped `IsUnread` to `false`. That command only ever changes `IsUnread`, never `Status` (a session really did finish — that fact doesn't change), so the color never had a code path that reacted to being acknowledged. The orange-red exists specifically to say "fresh, needs attention"; once read, that claim is no longer true, same reasoning already applied to `WaitingForInput`/"Idle" earlier today.

**Fix**: `StatusBrush` now renders `SessionStatus.Finished` as `OrangeRed` only `when IsUnread`, falling through to `Gray` once read — the status label still correctly says "Finished" (that's a true, permanent fact), only the alarm color goes away. Added the missing `[NotifyPropertyChangedFor(nameof(StatusBrush))]` on `_isUnread` so this actually updates live (the same class of bug as the `AggregateState`/`IconBrush` ordering issue fixed earlier today, caught here by inspection rather than a fresh repro, since the shape of the bug was now a known pattern to check for).

**Issue 2 implementation**: added `SessionItemViewModel.StatusIconSource`/`StatusIconVisibility`/`StatusDotVisibility`, shown only for `SessionStatus.WaitingForInput` ("Idle") rows. Rather than baking new art, this **reuses the existing `TrayIcon.Watermark.WaitingForInput.png`** asset directly — already a gold render of the user's bodhisattva SVG on a transparent background, previously kept loaded-but-unused in `TrayViewModel` specifically so a future reuse like this wouldn't need a re-bake (confirmed its background is genuinely transparent, not white, by inspecting the PNG's alpha channel directly: corner pixel `A=0`). `MainWindow.xaml`'s per-row template now has both the original `Ellipse` (`Visibility` bound to `StatusDotVisibility`, collapsed for Idle) and a new small `Image` (bound to `StatusIconVisibility`/`StatusIconSource`, visible only for Idle) layered in the same grid cell.

**Verified**: rendered the real `MainWindow`'s session list (demo scenario 0, which has both a `Finished`+unread row and a `WaitingForInput` row) to PNG before/after invoking the real `MarkSessionReadCommand` — confirmed visually and via direct property checks: the Finished row's dot/text/unread-dot/Read-button all correctly go from red+visible to gray+gone after "Read"; the Idle row shows the small gold glyph in place of a dot throughout. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

#### Follow-up (same day): the tray icon's own gray circle replaced with the gold glyph too

**Requested by the user**: "can you also make the gray tray icon into the golden glyph? the gray dot does not look good" — extending the same per-row change to the actual Win32 system tray icon itself, which still rendered every non-Working/non-AttentionNeeded state as a flat gray `GeneratedIconSource` circle.

**Why this was safe to do today specifically**: an image-based tray-icon background (`GeneratedIconSource.BackgroundSource`) was deliberately avoided back in §9.5 — at the time, `IconBrush`'s own doc comment warned that *any* `GeneratedIconSource` property change independently fired an un-awaited `ToIconAsync()` regeneration via `TaskbarIcon.IconSource`'s reactive binding, so a slower-to-render image background made the concurrent-regeneration race easier to hit. That whole reactive pipeline no longer exists as of this same day's earlier `IconSource`-removal follow-up — `MainWindow.xaml.cs` now sets every `GeneratedIconSource` property itself and calls the synchronous `ToIcon()` exactly once per real change. An image background no longer has a reactive path to race against at all, regardless of how long baking it takes.

**Implementation**: confirmed via reflection against the installed `H.NotifyIcon.Wpf` 2.4.1 DLL, then the library's real source (fetched from GitHub), that `GeneratedIconSource.BackgroundSource` (an `ImageSource`) exists precisely for this. Added `TrayViewModel.IconBackgroundSource` — `null` for `AttentionNeeded`/`Working` (where the flat color + glyph/badge is what actually needs to stay legible), and the same gold bitmap as the per-row icon (`WaitingForInputWatermarkImage` — reused again, not re-baked a third time) for every other state. `IconBrush`'s own gray branch became `Brushes.Transparent` for those states, since the image itself (transparent background, opaque gold figure) is now the entire visual. `MainWindow.xaml.cs`'s `UpdateTrayIcon()` now also sets `_iconGenerator.BackgroundSource`, and its `PropertyChanged` filter now includes `IconBackgroundSource`; `RecomputeAggregateState()`'s notification list (already fixed to notify after assigning `AggregateState`, see the follow-up above) now includes it too.

**Verified**:
- Re-ran the rapid-fire stress test (96 clicks) against the new image-background code path specifically: **zero exceptions** — confirms the reasoning above empirically, not just in theory.
- Rendered all 6 demo scenarios' tray icons to PNG: `AttentionNeeded`/`Working` unaffected (flat orange/green circle, unchanged); `Idle`/`NoSessions` now show the gold glyph on a transparent background, `IconBackgroundSource` correctly `null`/non-null in exactly the expected states.
- Downscaled the rendered icon to 16/24/32px (real taskbar sizes at 100%/150%/200% DPI) to check legibility at actual size — the figure (arms, halo, seated posture) stays recognizable down to 32px and is a visible, non-blank glyph even at 16px, not a blurred blob.
- All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

#### Follow-up (same day): read-Finished rows looked indistinguishable from genuinely Closed ones

**Reported by the user**: "when I click read, it becomes gray from red, but I think the session is still idle and not closed." A sharp catch, in the same spirit as the earlier "shouldn't it be idle and gray?" pushback: the previous fix (read `Finished` → `Brushes.Gray`) made that row use the exact same plain `Ellipse` dot as a genuinely `SessionStatus.Closed` session — silently conflating "this process has actually ended" with "this finished a turn, you've seen it, nothing further is known" (which is substantively the same uncertain, non-alerting situation as `WaitingForInput`/"Idle" — this app already can't claim anything more specific there either, per §2.3/§9.6's running reasoning).

**Fix**: `SessionItemViewModel` gained a private `IsIdleLike` check — true for `WaitingForInput` **or** a read `Finished` row — and `StatusDotVisibility`/`StatusIconVisibility` now key off that instead of `Status == WaitingForInput` alone. A read `Finished` row now shows the same gold glyph as an Idle row instead of the plain dot; a genuinely `Closed` row still gets the plain gray dot, untouched — preserving exactly the distinction the user pointed at ("idle" vs. "closed" are no longer the same gray circle). `StatusLabel` is deliberately left alone: a read row still correctly says "Finished", not "Idle" — only the dot/icon changes, since that label is still a true, permanent fact, unlike the glyph choice which is purely about alerting/non-alerting.

**Verified**: built four `SessionItemViewModel`s directly (unread-Finished, read-Finished, WaitingForInput, genuinely-Closed) and checked `StatusDotVisibility`/`StatusIconVisibility` for all four — matched expectations exactly (dot/icon/icon/dot). Rendered the real session list: unread-Finished still shows the red dot/text/Read button; read-Finished and WaitingForInput both show the gold glyph with gray text ("Finished"/"Idle" respectively, correctly still distinct in text); Closed shows the plain gray dot, visually distinct from the other two. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

#### Follow-up (next day): re-baked all artwork from the user's revised SVG

**Requested by the user**: "I have changed the SVG remotely, can you pull the latest SVG of guanyin and update it where it is used." `git fetch`/`git pull` brought in commit `7a3e820` ("Revise tray icon SVG for Guanyin meditation"), touching only `Assets/tray-icon-source.svg` — a materially different, more detailed design (six-armed figure with orchid-finger hand gestures via a reusable `<use>`'d path, a crown, a pointed flame-shaped halo, a bell-shaped robe, and a three-piece lotus seat) replacing the original simpler bodhisattva silhouette. The 4 baked PNGs (`TrayIcon.Watermark.{Working,Unread,Idle,WaitingForInput}.png`) are a separate, derived build artifact checked into the same `Assets/` folder — editing the `.svg` alone doesn't regenerate them, so every place that still showed the old artwork needed an explicit re-bake.

**Re-bake process**: recreated the same throwaway `SkiaSharp`/`Svg.Skia` console tool documented in §9.4 (not kept between sessions, per that section's own "throwaway, not a shipped dependency" note) — loads `tray-icon-source.svg`, string-replaces `currentColor` with each state's exact hex, renders via `SKSvg`/`SKCanvas` at 512×512 (matching the existing assets' resolution), and re-encodes to PNG. The 4 target hex colors were not re-guessed — sampled directly from the pixels of the *previously*-committed PNGs first (`#3CB371`/`#FF4500`/`#808080`/`#DAA520`), confirming they exactly match `IconBrush`'s/`IconBackgroundSource`'s existing palette before touching any file, so only the artwork changes, not the established state colors.

**Verified**:
- Baked to a staging folder first (not directly over the committed assets) and inspected each of the 4 output PNGs visually, plus re-confirmed transparent corners (`A=0`) and exact center-of-figure colors via direct pixel sampling — all 4 matched expectations before any real asset was touched.
- Downscaled the gold variant to 16/24/32px (real taskbar sizes) — the new, more detailed six-armed figure stays recognizable at 32px and is still a clear, non-blank gold glyph at 16px, the same legibility bar met by the original artwork.
- Only then copied the 4 files over the real `Assets/*.png`, rebuilt (0 warnings/errors), and ran the full test suite (69/69 pass — this is an asset-only change, `Core` untouched).
- Re-ran a rapid-fire stress test (48 clicks) against the real `TaskbarIcon`/`MainWindow` pipeline — zero exceptions, confirming swapping in new, more complex artwork didn't reintroduce the §9.6 GDI+ race (it couldn't architecturally, since nothing about the icon-generation call path changed — only the bitmap bytes `GeneratedIconSource.BackgroundSource`/`Image.Source` point at — but worth confirming empirically given this project's history with that exact bug).
- Rendered all three real places this artwork appears — the popup's big watermark, the per-session-row small gold glyph, and the tray icon's own `BackgroundSource` for all 6 demo scenarios — and visually confirmed the new six-armed design (crown, pointed halo, orchid-finger hands, lotus seat) displays correctly in every one, still color-matched per state.
- Real compiled app stopped, rebuilt, and relaunched; confirmed responsive.

### 9.7 Feature (2026-10-06): tray icon redesign — spinning ring + unread dot, replacing the flat color/text glyph

**Requested by the user**: "when there's working session, instead of using a green dot, we can use a spinning circle surrounding the golden guanyin glyph; when there's unread session, just add a small red dot at the bottom right of the glyph; No numbers are needed as they are too small to read." Scoped strictly to the actual Win32 tray icon itself — the popup's big watermark (`TrayViewModel.WatermarkImageSource`) and the per-session-row small gold glyph (`SessionItemViewModel.StatusIconSource`, added §9.6) were left untouched, since both already have their own working, recently-finished designs and the user's request was specifically framed around "the tray."

**Design decisions made without asking (stated here per usual practice)**:
- The ring's color is the same `MediumSeaGreen` already used everywhere else in this app for "working"; the dot's color is the same `#F44336` red already used for the per-row unread indicator — reusing the established palette rather than inventing new colors.
- The two overlays are independent and can both show at once (a session can genuinely be working *and* a different session can genuinely be unread, simultaneously) — drawn glyph, then ring, then dot, so the dot always stays visibly on top of the ring regardless of its current rotation angle, rather than picking one to "win" the way the old flat single-color icon was forced to.
- Spin cadence: 120ms/tick, 24°/tick (~8.3 updates/sec, full revolution ≈1.5s) — deliberately coarse, not 60fps-smooth: a 128px source canvas rendered down to a 16-32px taskbar icon doesn't benefit from finer steps, and every tick is one more icon regeneration.

**Implementation — a full replacement of the tray icon's rendering pipeline, not a tweak**: the glyph+ring+dot composition can't be expressed through `GeneratedIconSource`'s built-in "colored shape + centered text" renderer at all (no support for a dynamic rotating arc or a corner badge image), so the tray icon is now hand-composited directly with `System.Drawing.Graphics` in a new `MainWindow.xaml.cs` method, `ComposeTrayIconBitmap(bool isWorking, bool hasUnread, double spinAngleDegrees)`: draws the cached gold glyph bitmap (loaded once via `Application.GetResourceStream` into a GDI+ `System.Drawing.Bitmap`, not a WPF `ImageSource` — a different loading path was needed since WPF `Image` controls and `System.Drawing.Graphics` don't share a bitmap type), then conditionally a `DrawArc` ring and/or a `FillEllipse`+outline dot on top. A new `DispatcherTimer` (`_spinTimer`, 120ms) advances `_spinAngleDegrees` and calls `UpdateTrayIcon()` on each tick, but only *while* `TrayViewModel.IsAnyWorking` is true — started/stopped from `OnViewModelPropertyChangedForTrayIcon` (plus one explicit check in `InitializeTrayIcon()` for the edge case of a session already working at app startup, which has no "change" event to react to). `TrayViewModel` lost `IconGlyph`/`IconBrush`/`IconBackgroundSource`/`IconFontSize`/`IconTextMargin` entirely (all now-obsolete pieces of the old `GeneratedIconSource`-driven rendering, confirmed via grep to have no other consumers before removal) and gained one new property, `HasUnreadSessions` — the tray icon no longer needs a count at all, just whether the dot should show.

**A second, independent bug found while researching this — not hypothetical, confirmed from real source**: before writing any animation code, researched whether a timer ticking ~8 times/second to regenerate the icon could be done safely at all, given this project's entire day's history of GDI+ races from icon regeneration. Fetched and read both .NET's own `System.Drawing.Icon` source and H.NotifyIcon's real source (not reasoned about secondhand) and found: `Icon.FromHandle()` — used by both the old `GeneratedIconSource.ToIcon()` and this new code — always constructs a *non-owning* wrapper (`_ownHandle = false`); its `Dispose()` only calls the real Win32 `DestroyIcon` if the wrapper owns the handle, which a `FromHandle`-created one never does. H.NotifyIcon's own `TaskbarIcon.Icon` setter (`OnIconChanged`) dutifully calls `oldValue?.Dispose()` on every change — but since that `Icon` was always created via the same non-owning pattern, that `Dispose()` call is a complete no-op at the native level. **This means every single tray-icon update this app has made since this morning's `IconSource`-removal fix (§9.6) has leaked one native GDI icon handle (HICON)**, silently. At the old update frequency (only on real, relatively rare state transitions), this was practically unnoticeable — but at ~8 updates/sec from a continuous spin timer, it would have exhausted this process's default GDI handle budget (a few thousand) within low tens of minutes, a severe, novel failure mode this redesign would have introduced if shipped without catching this first.

**Fix**: `MainWindow` now tracks the native handle itself (`_currentTrayIconHandle`) and manually calls the real Win32 `DestroyIcon` P/Invoke on the *previous* handle, immediately after assigning the new one to `TrayIcon.Icon` — safe in that order because `Shell_NotifyIcon` (called synchronously inside that property setter) copies the icon's bitmap data internally, so the handle it was built from is never needed again once that call returns. `ShutdownTrayIcon()` also now destroys the final handle and disposes the cached glyph bitmap on app exit (technically moot — the whole process exiting reclaims all GDI handles regardless — but correct and cheap).

**Verified empirically, not just reasoned about**:
- Clean rebuild (0 warnings/errors); all 69 tests pass (`Core` untouched).
- Rendered all 4 state combinations (idle / working / unread / working+unread) at 4 spin angles each via reflection directly into the real, private `ComposeTrayIconBitmap` — confirmed visually: the ring and dot both render correctly, independently, and simultaneously, with the dot always staying visible on top of the ring regardless of rotation.
- Downscaled the working+unread combination to real taskbar sizes (16/24/32px) — both overlays stay distinguishable at 32px; still read as "gold glyph + a green accent + a red accent" at 16px, the same legibility bar already established for the plain gold glyph.
- Re-ran the established rapid-fire stress test (96 clicks against the real `TaskbarIcon`) — zero exceptions, confirming the rendering pipeline's "synchronous, UI-thread-only, one call at a time" invariant (established §9.6, unchanged by this redesign) still holds.
- Confirmed the "already working at app startup" edge case in an isolated process run: a fresh `TrayViewModel` loaded onto a Working scenario *before* `InitializeTrayIcon()` was ever called still started the spin timer immediately, not just on a later transition.
- **The critical new verification**: an extended soak test, isolated in its own single-window process run (an earlier combined-process version of this test transiently created a second, disposed `TaskbarIcon` for the startup-edge-case check, which then threw from a stray broadcast window message reaching its stale tooltip binding during the long pump — a harness-only artifact, since the real app only ever constructs one `MainWindow` for its whole process lifetime, not a product bug; re-isolated to eliminate the ambiguity rather than explain it away). Forced `IsAnyWorking=true`, sampled this process's real GDI object count (`GetGuiResources`, the actual Win32 API Task Manager's own "GDI objects" column reads from) every 5 seconds across 60 seconds of continuous spinning (~500 icon regenerations at the real 120ms cadence, with the tracked native handle value confirmed to actually change between samples — proving real regeneration was happening, not a no-op loop). Result: **the GDI object count stayed perfectly flat (13 → 13 → 13 ... → 13) for the entire 60 seconds** — had the handle leak not been fixed, this would have shown a count rising by very close to 1 per tick, reaching roughly 500 above baseline by the end. Also confirmed the timer correctly stops (and the ring disappears) the moment no session is working anymore.
- Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events in Windows Event Viewer.

#### Follow-up (same day): gold-to-white gradient ring, slowed down

**Requested by the user**: "can you use gold-to-white gradient for the circle and make it spin slower?" — "the circle" being the spinning ring (the user's own original wording called it "a spinning circle" — the unread indicator was always separately called "a small red dot", so there was no ambiguity about which overlay this meant).

**Color — a deliberate override of this app's earlier design assumption, not just a tweak**: the ring was flat `MediumSeaGreen` only because §9.7 defaulted to this app's established "green = working" convention when the user hadn't specified a color. Asked for gold-to-white instead, which makes the ring read as part of the same gold theme as the glyph it surrounds, rather than borrowing an unrelated color from elsewhere in the app.

**Why a flat `LinearGradientBrush` wouldn't actually work here**: GDI+'s gradient brushes paint across a flat 2D area in screen-space, not *along the length of a curved stroke* — a straightforward gradient brush behind the ring would stay fixed in screen-space while the ring rotated underneath it, so the same physical point on the ring would change color every frame depending on where it happened to be, not based on its position along the arc. Implemented the standard GDI+ workaround instead: `ComposeTrayIconBitmap` now draws the ring as 24 short, individually-colored arc segments (`RingSegmentCount`), each interpolated (`LerpColor`) from gold (`#DAA520` — the exact same gold already used for the glyph itself, not GDI+'s own slightly different built-in `Color.Gold`) at the leading edge to white at the trailing edge, each segment overlapping its neighbor slightly (`RingSegmentOverlapDegrees`) to hide any seam from floating-point rounding between adjacent arcs.

**Slower spin**: kept the same 120ms tick interval (no extra icon regenerations/resource cost — already soak-tested safe in §9.7) but reduced the per-tick rotation from 24° to 10°, taking a full revolution from ~1.8s to ~4.3s. Chose to slow the per-tick *distance* rather than lengthen the *interval* specifically so the motion would also look smoother (more, smaller steps) rather than just choppier-but-slower.

**Verified**:
- Rendered the real `ComposeTrayIconBitmap` (via reflection) at 8 angles spanning a full rotation — confirmed visually: a smooth gold-to-white gradient with no visible seams at any rotation.
- Downscaled to real taskbar sizes (16/24/32px) — the gradient stays distinguishable at 32px and still reads as a warm gold/white accent at 16px, the same legibility bar as before.
- Re-ran the rapid-fire stress test (96 clicks) — zero exceptions.
- Directly measured the real `_spinTimer`'s effect after one tick (via reflection on `_spinAngleDegrees`): advanced by exactly 10° in ~120ms, confirming the new, slower rate is actually in effect, not just intended.
- All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (next day): the icon looked small next to other tray icons

**Requested by the user**: "can you make the icon in the tray as large as possible, right now it seems smaller compared to other tray icons."

**Root cause, measured rather than guessed**: two compounding sources of wasted space. (1) `ComposeTrayIconBitmap`'s own glyph margin was `size * 0.12` (12% per side) — a leftover from when the glyph needed room for a centered text badge in the original `GeneratedIconSource` design; nothing about the current glyph+ring+dot composition actually needed that much clearance. (2) The baked glyph PNG itself has a small amount of fully-transparent padding baked into its own 512×512 canvas — measured directly (scanning alpha channel bounds) at ~90% content fill, i.e. ~5-10% wasted per side before this app's own margin is even applied. Combined, the visible figure occupied only roughly two-thirds of the icon's own canvas.

**Fix**:
- `MainWindow.xaml.cs` now crops the glyph bitmap to its actual visible-content bounding box once at load time (`CropToVisibleContent`, plain `GetPixel` alpha scan — acceptable since this runs once at startup, not per-frame), removing the artwork's own baked-in padding.
- `ComposeTrayIconBitmap`'s glyph margin dropped from 12% to 4% — just enough to avoid edge/anti-aliasing clipping.
- Tightening the glyph margin this much caused a new problem, caught by rendering before shipping: the working ring's positioning (already close to the canvas edge, unchanged) now visibly cut across the now-larger figure's own silhouette (confirmed via a render showing a gold/white bar slicing across the lotus seat). Fixed by changing the draw order — the ring is now drawn *first*, as a background layer, with the glyph drawn on top of it. Any part of the ring that falls under the figure's own opaque pixels is naturally occluded by it, while the part in the glyph's own transparent background still shows through — reading as a halo glowing from behind the figure rather than a ring competing with it for the same space.

**Verified**: rendered all 4 state combinations (idle/working/unread/working+unread) at full size and downscaled to real taskbar sizes (16/24/32px) — the figure is now dramatically larger at every size, the working-state halo reads as an intentional glow rather than an obstruction, and the unread dot remains clearly legible even at 16px. Re-ran the rapid-fire stress test (96 clicks) — zero exceptions. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (same day): clicking the unread dot now jumps straight to the terminal, and resuming auto-clears it

**Requested by the user**: "is it possible when there's unread message, when I click the icon with red dot, it redirects to the TUI directly? Or is there a way to make the dot disappear without me manually click read on the button?" — two related asks: a faster path from "I see the red dot" to actually being back in the conversation, and less manual bookkeeping to clear the dot.

**Design decisions (both additive, neither changes existing behavior in any case they don't explicitly target)**:
- A tray-icon click (or a finished-session balloon-tip click) now resumes the session directly in a real terminal pane — via `TrayViewModel.ResumeSessionCommand` specifically, not "resume with summary", since "redirects to the TUI directly" reads as continuing the exact existing conversation — **only when exactly one session is unread**. Zero unread (nothing to jump to) or two-or-more unread (no way to guess which one) both fall back to the existing "just show the popup" behavior unchanged, so this is never a surprising guess.
- A click while the popup is *already open* still just closes it, unchanged — the redirect only ever applies to an *opening* click. Unconditionally hijacking every click would otherwise mean clicking the icon to dismiss an already-open popup could instead unexpectedly launch a terminal.
- Resuming a session (either "Resume with history" or "Resume with summary", from the per-row "⤴ Resume" button/menu, *or* the new one-click redirect above, since it's the same underlying command) now also marks that session read automatically on a successful launch — resuming is at least as strong a signal of "I've seen this" as the dedicated "✓ Read" button, which remains available separately for acknowledging without opening a terminal at all. Deliberately skipped if the launch itself failed (e.g. Windows Terminal isn't installed) — the user never actually got to see anything in that case, so marking it read would misrepresent what happened.

**Implementation**: `TrayViewModel.TryLaunchAsync` now returns `bool` (previously `void`-equivalent `Task`) so `ResumeSession`/`StartSessionFromSummary` can tell whether to call the existing `MarkSessionRead` afterwards. `MainWindow.xaml.cs` gained `ShowPopupOrResumeSingleUnreadSession()` — checks `TrayViewModel.UnreadCount == 1` via a property pattern, resumes `Sessions.Single(s => s.IsUnread)` if so, otherwise falls back to the existing `TogglePopup(forceShow: true)` — now called from both the single-click debounce timer (after its existing "already visible → just hide" check, unchanged) and the balloon-tip-click handler (previously always forced the popup open unconditionally; a balloon is itself already about one specific just-finished session, so it gets the same smarter routing).

**Verified**: built a harness simulating real user interaction (invoking the actual private `TrayIcon_TrayLeftMouseUp`/`TrayIcon_TrayBalloonTipClicked` handlers via reflection, through the real 250ms debounce timer, against a real `MainWindow`) across 5 cases: (1) exactly one unread session → popup stayed hidden, the correct session was resumed, its `IsUnread` flipped to `false` automatically; (2) zero unread → popup shown, no resume attempted; (3) two unread → popup shown (correctly ambiguous, no guessing); (4) popup already visible → click just hid it, no redirect attempted (regression check: the pre-existing toggle gesture still works exactly as before); (5) balloon-tip click with one unread session → redirected the same as a tray-icon click. All 5 matched expectations exactly. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (same day): two corrections — resuming was the wrong action, and the notification needed to persist

**Reported by the user**, two issues from actually using the feature above: "But the unread session is still open, I don't need to open a new terminal" and "I see there's a small reminder box popped out when a session is complete, but it disappeared very quickly, can we make it stay there?"

**Issue 1 — the one-click redirect's "resume" action was actually wrong, not just unwanted, confirmed from this app's own detection semantics, not merely the user's preference**: checked `SessionDetectionEngine`'s own doc comments (written by this project, not re-guessed) — `SessionChangeKind.Finished` (the transition that sets `SessionItemViewModel.IsUnread`) is *only ever* assigned to a session that is *still in that poll's genuinely-open set*, i.e. its real Copilot CLI process is still running right now, just idle after finishing a turn. `SessionChangeKind.Closed` is the separate, distinct transition for an actual process exit. So *every* unread session in this app, by construction, already has its own real terminal/TUI sitting open somewhere — launching a second one via "Resume" (what the previous follow-up shipped) was always creating a redundant pane, never taking the user anywhere new. The user's correction wasn't a style preference; it was pointing out the feature's premise was wrong.

**Fix**: `MainWindow.ShowPopupOrResumeSingleUnreadSession()` → renamed `ShowPopupAndClearSingleUnreadSession()` — no longer calls `ResumeSessionCommand`. Now: if exactly one session is unread, marks it read (`MarkSessionReadCommand`) so its dot is already cleared by the time the popup appears, then always shows the popup (same as before). The 0-unread/2+-unread fallback behavior is unchanged. The separate "resuming auto-marks read on success" change from the previous follow-up is *kept* — if a user genuinely wants a second pane via the explicit "⤴ Resume" button, that's still available and still clears the dot.

**Issue 2 — the notification needed to actually persist, which the native balloon API cannot do**: investigated before changing anything. H.NotifyIcon's `TaskbarIcon.ShowNotification`'s own `timeout` parameter is explicitly documented (fetched from the library's real source) as deprecated since Windows Vista: "Notification display times are now based on system accessibility settings. The system enforces minimum and maximum timeout values... currently set at 10 seconds and 30 seconds" — not overridable by an app, regardless of what's passed. The library does have an internal `ResetBalloonCloseTimer()` meant to work around this (re-sends the same notification every 25 seconds to keep it alive) — tried this first empirically, via a real `TaskbarIcon` with `TrayBalloonTipShown`/`TrayBalloonTipClosed` subscribed, to check whether each 25-second refresh would visibly re-pop/flicker the notification. Neither event fired even once on this machine's Windows version (for the initial notification or any refresh), meaning they don't appear to route through the modern Action-Center-style toast presentation at all here — so this couldn't actually answer the question, and shipping it anyway risked making the complaint *worse* (a notification that visibly re-pops every 25s instead of one that merely vanishes quickly).

**Fix**: switched to H.NotifyIcon's other, completely different notification mechanism — `TaskbarIcon.ShowCustomBalloon(UIElement, PopupAnimation, int? timeout)` — a real, plain WPF `Popup` hosting a `UIElement` this app builds and fully controls, with `timeout: null` ("keep the balloon open indefinitely" per the library's own doc comment). No refresh trickery, no uncertainty: nothing about its lifetime is mediated by Windows' own notification pipeline at all. `NotificationService` now builds a simple `Border` (title bold, body underneath, "Click to view" hint, drop shadow) instead of calling `ShowNotification`; a new `BalloonClicked` event (App-layer-only, not on `INotificationService` — same reasoning as `AttachTrayIcon` already not being on that interface) reports which exact `SessionId` was clicked, since a notification is always unambiguously about one specific session (unlike the tray-icon click's own "exactly one unread" heuristic, which doesn't apply here — this is more precise, not just reusing the same guess). `MainWindow` subscribes once in `InitializeTrayIcon()`, marks that specific session read, and shows the popup. The now-dead native `TrayBalloonTipClicked` XAML wiring/handler (would never fire again) was removed rather than left as unused dead code.

**Verified**: a harness covering both fixes against real components: (1) single click with exactly one unread session → popup shown, **no terminal launched** (confirmed via a tracking fake launcher recording zero calls), session marked read; (2) a real custom balloon shown via `NotificationService.NotifySessionFinishedAsync` — confirmed open (`TaskbarIcon.CustomBalloon.IsOpen == true`) immediately, and **still open after 40 real seconds** (deliberately chosen to exceed the old native balloon's 10-30s OS-enforced maximum, proving genuine OS-independent persistence, not a lucky coincidence); (3) simulating a real click on the balloon's content → `BalloonClicked` fired with the correct session id, the popup opened, and the balloon closed. All matched expectations exactly. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (same day): dismissing the notification is the whole interaction — it no longer also opens the popup

**Reported by the user**: "Maybe there's no need to click to review, I can just close it to acknowledge that as I cannot directly jump to the open terminal." Direct, correct feedback on the previous follow-up's own design: clicking the balloon marked the session read *and* force-opened the popup, reasoning that dismissing should also let the user "go look at it" — but since this app has no way to actually focus/activate the session's real, already-open terminal window (nothing it can show the user actually takes them anywhere closer to that terminal), forcing a window open on every single dismissal added UI the user didn't ask for without adding any real navigation value of its own.

**Fix**: `MainWindow.OnNotificationBalloonClicked` no longer calls `TogglePopup(forceShow: true)` — it only marks the specific session read now. Dismissing the notification (clicking it) *is* the entire interaction: purely "acknowledge", nothing more. The popup remains one separate click away (the tray icon itself) for anyone who does want to look at the list. The balloon's own hint text changed from "Click to view" to "Click to dismiss" to match — it no longer implies clicking takes you anywhere.

**Verified**: re-ran the same kind of real-component harness as the previous follow-up — showed a real custom balloon for a specific unread demo session, confirmed the popup was hidden beforehand, simulated a click on the balloon, and confirmed afterwards: the session's `IsUnread` flipped to `false` (acknowledged), the popup stayed hidden (`window.IsVisible == false` — no longer force-opened), the balloon closed, and the hint text read exactly "Click to dismiss". All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (same day): the popup watermark matches the gold per-row glyph too

**Requested by the user**: "when there's open session in golden, can you also make the watermark golden?" — the per-session-row gold glyph (`SessionItemViewModel.StatusIconSource`, added §9.6's later follow-up for idle-like rows) had no equivalent on the big popup watermark, which still fell through to plain gray for every one of those rows.

**Implementation**: made `SessionItemViewModel.IsIdleLike` public (was `private`) so `TrayViewModel.WatermarkImageSource` can share the exact same "what counts as idle-like" definition — `WaitingForInput`, or a read `Finished` row — rather than risking two independently-maintained copies of that condition drifting apart over time. `WatermarkImageSource`'s fallback arm (previously always the plain gray `IdleWatermarkImage`) now checks `Sessions.Any(s => s.IsIdleLike)` and selects the existing gold `WaitingForInputWatermarkImage` asset when true — placed below `AttentionNeeded`/`Working` in priority (an unread or actively-working session is still more notable than one merely sitting idle), but now a distinct tier above plain gray rather than being folded into it. Explicitly *not* a reversal of `TrayAggregateState.WaitingForInput`'s own "stub, not deleted" stance (§9.6) — that stub is specifically about *claiming to know* a session is "awaiting a reply", a thing Copilot CLI's own data still can't distinguish from "long done"; this change is just a visual-consistency match for rows this app already shows in gold today, not a new freshness/urgency claim.

**Verified**: exercised 5 cases against the real `TrayViewModel`: a single `WaitingForInput` session → watermark now gold (was gray); a mix with an unread session present → still orange (unread correctly still wins); a `Working` session alongside a separate idle-like one → still green (working correctly still wins over gold); a read-`Finished`-only session (idle-like via the *other* branch of the shared condition, not `WaitingForInput`) → also correctly gold, confirming the shared definition covers both cases identically; an empty session list → still plain gray (nothing to match). Rendered the real popup for the gold case and visually confirmed the watermark now matches the per-row glyph. All 69 tests pass; solution rebuilds clean. Real compiled app stopped, rebuilt, and relaunched; confirmed responsive, no new crash events.

#### Follow-up (same day): removing "unread" entirely — the dismissable balloon already covers it

**Requested by the user**: "It looks like with the dismissable balloon, I don't really need the red dot behaviour in the tray icon or unread-related features." A direct consequence of the previous two follow-ups above: once dismissing the persistent balloon became the *entire* acknowledgment interaction, the separate unread-tracking machinery (red tray dot, orange-when-unread row color, "✓ Read"/"Mark all read" buttons, the `AttentionNeeded` priority tier) had nothing left to do that the balloon didn't already cover — it was now redundant complexity, not a distinct feature.

**Scope, decided without asking (stated here per usual practice)**: removed *only* the "unread" concept (`SessionItemViewModel.IsUnread`/`UnreadDotVisibility`, `TrayViewModel.UnreadCount`/`HasUnreadSessions`/`MarkAllRead`/`MarkSessionRead`, `TrayAggregateState.AttentionNeeded`, the tray icon's red-dot overlay, the orange watermark tier). Deliberately did **not** touch the separate, unrelated "remove from view" feature (`TrayViewModel.RemoveSession`/`PersistReadMarkerAsync`, `SessionReadMarker.IsDismissed`) — confirmed by re-reading its own doc comments first: it's an explicit user action ("✕ Remove" button) with its own purpose (hiding a session from the list, never touching the real Copilot session), unrelated to read/unread state, and the user's own wording ("unread-related features") didn't ask for it.

**Consequence worth calling out explicitly**: `SessionItemViewModel.IsIdleLike` (added §9.6 to distinguish a *read* `Finished` row from a genuinely `Closed` one) simplified from `WaitingForInput || (Finished && !IsUnread)` to unconditionally `WaitingForInput || Finished` — every `Finished` row is now idle-like (gold glyph, gray text) regardless of read state, since there's no more read state to condition on. `Working` (green) and `Closed` (plain gray dot) remain the only other visually distinct states.

**Implementation**:
- `SessionItemViewModel`: removed the `isUnread` constructor parameter, the `IsUnread`/`UnreadDotVisibility` members, and the `Finished when IsUnread => OrangeRed` branch from `StatusBrush` (now unconditionally gray for `Finished`, same as `WaitingForInput`/`Closed`).
- `TrayViewModel`: removed `UnreadCount`, `HasUnreadSessions`, `MarkAllRead`/`MarkAllReadCommand`, `MarkSessionRead`/`MarkSessionReadCommand`, the `UnreadWatermarkImage` field/asset, and the `AttentionNeeded` branch from both `WatermarkImageSource` and `RecomputeAggregateState()`. `ToolTipText` no longer appends "N need attention". `ResumeSession`/`StartSessionFromSummary` no longer call `MarkSessionRead` after a successful launch (that call's entire purpose was clearing a flag that no longer exists) — `TryLaunchAsync` reverted from `Task<bool>` back to a plain `Task` since nothing reads its success/failure anymore. All demo-scenario and live-session-builder `SessionItemViewModel` construction call sites updated to drop `isUnread:`.
- `TrayAggregateState`: removed the `AttentionNeeded` enum value entirely (not kept-as-stub like `WaitingForInput` — this one's being deliberately deleted per explicit request, not earmarked for a future real implementation).
- `MainWindow.xaml`: removed the "Mark all as read" context-menu item, the per-row red unread `Ellipse`, the "✓ Read" button, and the bottom popup's "Mark all read" button (dropped a now-empty Grid column in the row template along the way).
- `MainWindow.xaml.cs`: `ComposeTrayIconBitmap` lost its `hasUnread` parameter and the red-dot-drawing block entirely — the tray icon now only ever composites the glyph plus the optional working ring, nothing else. `ShowPopupAndClearSingleUnreadSession()` — whose entire purpose was clearing a single unread session's flag before showing the popup — reduced to exactly what `TogglePopup()` already does unconditionally, so it was deleted outright and the single-click timer now just calls `TogglePopup()` directly. `OnNotificationBalloonClicked` (whose only action was `MarkSessionReadCommand.Execute(...)`) was removed along with its `BalloonClicked` subscription/unsubscription.
- `NotificationService.cs`: removed the now-purposeless `BalloonClicked` event entirely — dismissing the balloon (closing it) no longer needs to report anything back to `MainWindow`; its click handler now just calls `CloseBalloon()`.
- Deleted the now-fully-orphaned `Assets/TrayIcon.Watermark.Unread.png` asset and its `.csproj` `<Resource>` entry (unlike `WaitingForInput`'s gold asset, which stays loaded-but-unused as a deliberate future stub, the orange/unread concept is being actively removed, not earmarked for later).

**Verified**:
- Clean rebuild: 0 warnings, 0 errors. All 69 existing `Core.Tests` pass unchanged (this was an App-layer-only change).
- A scratch harness constructing the real, compiled `SessionItemViewModel`/`TrayViewModel` types (via their new, shorter constructor/real DI dependencies with fakes) confirmed, via reflection against the actual compiled assembly: `IsUnread`/`UnreadDotVisibility`/`UnreadCount`/`HasUnreadSessions`/`MarkAllReadCommand`/`MarkSessionReadCommand` are all completely gone (not just hidden); `RemoveSessionCommand` (the kept, separate feature) still exists; `TrayAggregateState` has exactly `NoSessions`/`Idle`/`Working`/`WaitingForInput` (no `AttentionNeeded`); a `Finished` and a `WaitingForInput` row now render *identically* (same gray `StatusBrush`, same gold `StatusIconSource` reference, same dot/icon visibility); `Working` and `Closed` remain visually distinct from both and from each other. Cycled all 6 "Cycle demo data" scenarios (0-5, including the live one) against the real `TrayViewModel` — no exceptions, `WatermarkImageSource` always non-null, `ToolTipText` never mentions "need attention", and every resulting row's `IsIdleLike` matched the new, simpler rule exactly.
- Real compiled app stopped, rebuilt, and relaunched against this machine's real `.copilot` data; confirmed responsive 10+ seconds later, no new Application Error events in Windows Event Viewer.

#### Follow-up (2026-10-08): ring removed, glyph rendered at the shell's exact size and emboldened, "shine" pulse while working

**Requested by the user**: "for the working state, right now we have circle spinning, and the icon seems smaller than other icons in the tray; I wonder if we can remove the circle and enlarge the icon, and when it's in working state, it flashes from golden to golden-white like shining?"

**Root cause of "smaller than other icons", measured rather than guessed**: the earlier 2026-10-07 fix (crop + 4% margin) was already near the ceiling of what a bigger canvas fill could achieve (the glyph occupied ~92% of its 128px canvas). The real problem was downstream of the canvas: this app handed the shell a 128px HICON and let Windows shrink it to the tray's 16×16 (`SM_CXSMICON` at this machine's 100% DPI). Screenshotting the real notification area (taskbar is a *light* theme here, ~`#EEEEEE`) and comparing against candidates showed the shell **point-samples** (hard-edged, un-antialiased, broken pixels): the figure's thin halo/hands mostly dropped out, leaving a sparse "crown" that covered only ~31% of its 16×16 slot (gold-ish bbox 15×13) — next to full-bleed neighbours like Teams. A properly area-averaged render was smooth but *pale* (~33% coverage) because the figure is mostly thin strokes; a slightly emboldened silhouette (0.30–0.45 target-px dilation) reached ~55–61% coverage while keeping the finger gaps/halo notches readable.

**Decisions made without asking (autopilot; all are single constants)**:
- `BoldnessPixels = 0.35` (in `TrayIconRenderer`) — a dilation radius expressed in *target* pixels, so larger icons (125–300% DPI) keep proportionally more detail rather than getting fatter.
- `ShineColor = #FFE9A0` (golden-white). On the light taskbar a whiter peak (`#FFF1BF`) measured ΔE 26 against the bar and looked ghostly in a mock-up pasted into the real tray strip; `#FFE9A0` measures ΔE 38 against `#EEEEEE`, ΔE 89 against a dark `#202020` bar, and ΔE 39 from the resting gold `#DAA520` — a clear pulse that stays visible on both themes. Whiter is a one-constant change if the taskbar theme changes.
- `PulsePeriodMs = 1600`, 100ms tick (~10 icon updates/sec, only while `IsAnyWorking`), raised-cosine so it eases in and out ("shining", not blinking). The icon rests at plain gold (shine 0) whenever nothing is working.

**Implementation**:
- New `TrayIconRenderer` (`src\CopilotSessionTray.App\TrayIconRenderer.cs`, pure `System.Drawing`, no WPF dependency): reads the glyph PNG once, finds its visible bounds with a `LockBits` scan (replacing the old per-pixel `GetPixel` crop), fits them into a square 256×256 alpha master (the artwork is a flat `#DAA520` + alpha, so only the alpha plane and the averaged opaque colour are kept — the source bitmap is disposed right after load, so there's no long-lived GDI bitmap any more). Per target size it caches an emboldened (soft-disc grayscale dilation), exactly area-averaged coverage plane; `Render(sizePx, shine)` then writes the BGRA pixels directly, lerping the glyph colour toward `ShineColor`. Full-bleed (0% margin), aspect-preserving.
- `TrayIconRenderer.GetShellIconSizePx()` asks Windows for the real tray icon size: `GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(Shell_TrayWnd))`, falling back to `GetSystemMetrics` (no taskbar yet, e.g. run-at-startup before explorer) and 16. Verified mapping: 100/125/150/175/200/250/300% → 16/20/24/28/32/40/48 px. `MainWindow` re-queries it on every update, plus a slow 5s `_iconSizeWatchTimer` that re-renders whenever the queried size no longer matches what was last rendered, so a DPI/monitor change is picked up even while idle. (An event hook — `SystemEvents.DisplaySettingsChanged`, debounced — was the first attempt, but an independent review noted it isn't guaranteed to fire for a pure scale-factor change; polling three cheap Win32 calls every 5s needs no static event, thread marshalling or debounce, and doesn't depend on any notification arriving.)
- `MainWindow.xaml.cs`: deleted `ComposeTrayIconBitmap`, all ring constants (`RingSweepDegrees`/`RingSegmentCount`/`RingSegmentOverlapDegrees`/`RingGoldColor`/`LerpColor`), `_spinTimer`/`_spinAngleDegrees`, `LoadGlyphBitmap`/`CropToVisibleContent` and the cached `_glyphBitmap`. Added `_pulseTimer` + a `Stopwatch` clock; `UpdatePulseTimer()` only restarts the clock on a real stop→start transition (not on every `IsAnyWorking` PropertyChanged — `RecomputeAggregateState()` raises it unconditionally, including when the value didn't change, which the old code answered by re-zeroing the spin angle each time). `UpdateTrayIcon()` keeps its pre-existing synchronous, UI-thread-only, one-call-at-a-time structure and manual HICON destroy-after-replace — only the bitmap source changed. Stale comments in `MainWindow.xaml`, `SessionItemViewModel`, `TrayAggregateState` and `TrayViewModel` that referenced the ring/`ComposeTrayIconBitmap` (one of which already wrongly claimed the ring was green) were corrected.

**Verified** (against the real compiled app, real tray, real data — not just a harness):
- Clean rebuild: 0 warnings, 0 errors; all 69 existing `Core.Tests` pass unchanged. The production `TrayIconRenderer.cs` was *linked* (not copied) into scratch harnesses to render the shine ladder and 16/20/24/32/48px sizes on light and dark backgrounds: steady-state `Render(16)` costs 0.019 ms/frame; first render per size (dilation) 8–50 ms, once.
- Real tray, before → after: icon coverage ~79 → ~171 of 256 pixels, now filling the whole 16×16 slot (was 15×13 of sparse pixels), no ring pixels at all.
- Real tray, sampled every 50-100ms: while working, the icon's mean blue channel swings smoothly 67 ↔ 160 (gold ↔ golden-white) on a ~1.6s cycle with a constant ~170-172 inked pixels per frame (a stable silhouette, no flicker); when idle it's exactly constant (`(203,164,67)`), and returning to idle from working settles on that steady gold immediately (3/3 transitions). A 20s continuous scan (400 frames) of the working state found zero blank/black frames.
- Handle health: a 75s steady-state soak (~750 icon updates) held GDI at 26 (range 26-29) and USER at ~40 (range 39-43) — a leak of even one handle per update would have shown +750; after 154 minutes of uptime (the live monitor reporting this very CLI session as "working", i.e. pulsing much of that time) the real process sat at GDI 31, USER 42, 84MB working set, 62s of total CPU, still responsive.
- Stress: 150 rapid "Cycle demo data" presses via UI Automation at 25ms spacing (popup auto-hid and was reopened 25 times) — app still alive and responsive, no crash events in the Application log (the original GDI+ crash class this app had to design around).
- **Not verified live** (stated rather than glossed over): a graceful *Quit* through the tray menu and a real DPI/monitor change. The workstation locked partway through testing (lock screen confirmed: `LogonUI` running; the one-off black frames seen at the start of one sampling run coincided with it, and 3/3 repeat runs plus the 400-frame scan were clean), after which GUI automation could no longer reach the desktop; a synthetic `WM_ENDSESSION` didn't make WPF shut down. The shutdown path only gained timer stops and removal of the old `_glyphBitmap.Dispose()`; the DPI path is covered by the verified size mapping plus rendering at all those sizes. An independent code review of the whole change (pixel math at 16-300px, stride/byte order, thread-safety, P/Invoke signatures) found no confirmed bugs.

#### Follow-up (2026-10-08, later): whole-icon tint replaced by a radial glow from the folded palms; a premultiplied-alpha icon bug fixed

**Requested by the user**: "it's shining too much, can you make it like there's a source of light in the center of the icon, like from the folded palms of the guanyin? There should be some gradient from center to outwards."

**Why the previous pulse was "too much", measured**: it tinted *every* pixel of the figure toward `#FFE9A0` at the peak — ink-weighted mean ΔE from the resting gold of 39.1 — so on the light taskbar the whole figure washed out toward the bar colour instead of reading as a light. This supersedes the whole-glyph tint described in the section above (the exact-size/emboldened rendering from that section is unchanged).

**Design**: the light source is the centre of the folded palms, located rather than eyeballed — flood-filling the artwork's transparent regions, the palms' outline strokes are the only enclosed holes near the middle: bbox [194..317]×[222..317] of the 512×512 canvas, centre (255.5, 269.5), i.e. 49.9% across / 54.5% down the figure's visible 462×469 bounds (`PalmsCenterX`/`PalmsCenterY`). Per target pixel, `weight = glow × exp(−(d/σ)²)` with `d` the distance from that point in icon-normalised units and σ growing 0.16 → 0.24 with `glow`; the pixel colour is lerped from the glyph's gold toward `#FFF2C0` by `weight`. Coverage/alpha are untouched, so the silhouette is identical in every frame. `MainWindow.CurrentGlow()` = `PulseFloor (0.25) + 0.75 × raised-cosine` over the unchanged 1.6 s period, so the light breathes but never fully goes out while working; idle is `glow = 0`, byte-for-byte the plain gold. Six candidates (narrow/broad σ, core + halo, three core colours) were compared on light and dark bars before choosing: the near-white core (`#FFF8E0`) sat only ΔE 12.9 from the light bar and risked reading as a hole in the figure, the broad ones (σ 0.24–0.34) drifted back toward the washed-out look (mean ΔE 22.5), and the chosen one keeps the arms/halo/lotus saturated gold — ink-weighted mean ΔE at the peak 13.3 (was 39.1) with the core still ΔE 51 from the gold.

**A second, pre-existing bug found by checking the real tray against the renderer**: the first real-tray comparison fit the lab render with a 17/255 per-channel residual; an "alpha applied twice" model fit with 0.46. Cause: `Bitmap.GetHicon()` stores the icon's colour bitmap *premultiplied* (read back with `GetIconInfo`/`GetDIBits`: R=109 for a straight R=218 at alpha 128), while the shell treats it as straight alpha, giving colour × α² — dim, olive anti-aliased edge pixels (a hand pixel at α≈0.8 came out R=186, darker than the gold can ever be over a light bar). Fixed with `TrayIconRenderer.CreateIconHandle`: `CreateIconIndirect` over a 32bpp DIB section holding the straight-alpha pixels (plus an all-opaque 1bpp mask); `MainWindow.UpdateTrayIcon` calls it instead of `GetHicon()`, with the destroy-after-replace handle logic unchanged.

**Verified** (real compiled app, real taskbar, live data — the live monitor was reporting this very session as "working"):
- Build 0 warnings; all 69 `Core.Tests` pass. Production renderer output matched an independent hand calculation at every probed pixel (glow 0 = exactly `#DAA520`); a frame costs 0.03 ms at 16 px.
- Frame-by-frame, the real taskbar was captured every 15–40 ms and each frame best-matched against the production renderer at glow 0…1: residual **0.41/255** (was 17.2), glow estimate **0.25 → 1.00** (exactly the designed floor and peak), period **1599 ms** (six rising crossings in a 9.6 s capture). Per-pixel blue-channel swing over a cycle falls from 118 at the centre to 78, 46, 14, 7 and 5 toward the edges — the radial gradient — so the periphery stays gold. Idle: perfectly static (spread 0, centre `[218,165,32]`, 0 outliers in 75 frames).
- `CreateIconHandle` round-trips pixel-exact at 16/20/24/28/32/40/48/64 px, and 3,000 create/destroy cycles left the GDI object count at 7 → 7.
- Churn: 421 frames at ~15 ms while 150 rapid "Cycle demo data" presses flipped the icon between idle and working — 0 outliers, max residual 0.50, i.e. never a blank or half-replaced frame; app responsive, no crash events. Soak: ~750 icon creations over 75 s in the working state held GDI at 23 (range 23–25) and USER at 37, and the icon was still pulsing afterwards.
- Noted for honesty: one early idle capture showed a few wildly-off frames (max residual 195); it did not reproduce in three repeats of the same sequence nor in the churn run, so it was most likely a transient overlay on the taskbar strip rather than the icon.

#### Follow-up (2026-10-08, later): stacked, individually dismissible notification cards

**Requested by the user**: "when there are more than 1 completed tasks, can you stack the balloons so I can see all of them and dismiss one by one?"

**Root cause of "only the latest is visible"**: H.NotifyIcon's `ShowCustomBalloon` unconditionally calls `CloseBalloon()` first, so one balloon per notification can only ever show one — a second finish while the first was still undismissed destroyed it. The popup also places itself with its bottom-right corner just above the taskbar (the anchor is the taskbar's top-right corner and WPF flips an off-screen `AbsolutePoint` popup), which turns out to be exactly what a growing stack needs.

**Implementation** (`NotificationService`): one H.NotifyIcon balloon whose content is a `ScrollViewer` → `StackPanel` of cards, one card per session. A new session's card is inserted at the top; a repeat completion from a session that already has a card updates that card in place instead of adding a duplicate (so the stack is bounded by the number of sessions); clicking a card removes just that card; the balloon closes with the last one. If the popup is ever closed from outside while cards remain, the next notification re-opens it with all of them. Calls are marshalled to the UI thread, since the service now holds shared state. Hovering a card highlights it so it's clear which one a click will dismiss.

**Decisions made without asking (autopilot)**: *newest on top* — with a bottom-anchored popup, adding at the top means existing cards never move under the cursor when a new one arrives; *one card per session* (rather than one per completion) so a chatty session can't flood the screen; *scroll cap* at work-area height − 40 px, because beyond that WPF would clamp the popup and cut off cards that could then never be reached; no "dismiss all" (not requested).

**Verified**: a real-WPF harness that links the production `NotificationService.cs` unchanged and drives a real `TaskbarIcon` and real popup — 27 checks, all passing, plus screenshots. Three cards arriving in the same poll tick all show, newest on top; the popup's bottom edge stays at 999 (taskbar top 1002) while its top moves 908 → 725, and the original card's screen position changes by <1.5 px; a repeat completion refreshes its card in place; dismissing the middle card removes only it and the bottom card stays put; real mouse hover highlights exactly one card and a real click dismisses just that one; clicking the last card closes the popup; re-opening after close, restoring all cards after an external close, and a notification raised from a thread-pool thread all work; 18 cards stay on-screen (top 37, bottom 999) and scroll (scrollable height 680); shutting down with cards open is clean. Build 0 warnings, 69 tests pass.
- **Not observed end-to-end in the running app at the time of writing**: the `TrayViewModel` → `NotificationService` call is unchanged (same interface), and the relaunched app is healthy, but a real "session finished" card hadn't been seen there yet, because triggering one would mean tampering with the real `.copilot` session data (which this app only ever reads). *Update*: a real "session finished" card for a real session did appear in the running app later the same day, which confirmed the `TrayViewModel` → `NotificationService` path with live data. The card popup itself has since been replaced by the balloons in the next follow-up.

#### Follow-up (2026-10-09): colorful balloons that rise up the right edge

**Requested by the user**: "is it possible to make the balloon look nicer? Maybe turn it into a real balloon with colors, and rise up to from bottom right to top right?"

**Why this isn't a restyle of the card popup**: H.NotifyIcon's custom balloon is one popup, anchored to the taskbar's top-right corner, that `ShowCustomBalloon` destroys and recreates on every call — a single rectangle that can't hold several independently moving pieces, and bottom-anchored, the opposite of "rise to the top right". So `NotificationService` no longer uses `TaskbarIcon` at all (the `AttachTrayIcon` call and the service's constructor parameter/field in `MainWindow` are gone; DI is just `AddSingleton<INotificationService, NotificationService>()`) and each notification is its own small window.

**Interpretation, decided without asking (autopilot)**: balloons launch from just above the taskbar at the right edge, float up with a gentle sway, and gather at the top right, stacked top-down in the order the sessions finished; clicking one pops it and the rest rise to fill the gap.

**Implementation**:
- `BalloonWindow` (new — `BalloonWindow.xaml`/`.cs`): a transparent (`AllowsTransparency`), `Topmost`, `ShowActivated=False`, `ShowInTaskbar=False` window, 268×312 px — a 200×244 vector balloon plus a 34 px transparent margin for the sway/hover/pop scaling. The art is hand-built XAML: an egg-shaped body path shaded by a radial gradient derived from the balloon colour (lighter toward the upper left, darker toward the rim), two gloss highlights, a knot, and a curly string drawn twice (a dark underlay and a light core, so it reads on light and dark backgrounds). Title (≤2 lines), body (≤2 lines) and a "Click to pop" hint sit centred on the body; WPF has no `TextBlock.MaxLines`, so trimming is a fixed `LineHeight` (`LineStackingStrategy=BlockLineHeight`) plus a `MaxHeight`. Text is white unless its contrast against the balloon colour is under 3.2:1, then near-black (the yellow one). `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` are added in `OnSourceInitialized` (through the 32-bit `Get/SetWindowLongW`, so it's safe on x86 too), so a balloon never takes focus from what the user is typing in. The window title is the notification title, so UI Automation names match what's drawn.
- Motion: on launch `Top` animates from just above the taskbar to the balloon's slot with a sine ease-out over `clamp(900 + 0.9 × distance, 900, 2400)` ms; meanwhile a render transform inflates it (0.55 → 1 with a slight overshoot, 420 ms), fades it in (260 ms) and sways it (±14 px of drift, ±5° of tilt across the rise), followed by a small settle bob. The sway is a render transform rather than window movement, which keeps the window's x constant. Hover scales the balloon 1.06×; a click pops it (1.28× scale + fade over 150 ms, then `Close()`); a same-session refresh wobbles it instead. `MoveTo` slides a balloon to a new slot (380 ms, cubic ease in-out), and an animation-id token stops a superseded slide's completion handler from snapping the window to a stale position.
- `NotificationService` (rewritten): holds the live balloons and assigns slots top-down in arrival order — the first at the top (`TopInset` 48 px, clear of the title-bar caption buttons), then one every 172 px (160 px body + 12 px gap), `RightInset` 18 px from the work area's right edge. When the full pitch wouldn't fit above `BottomInset` (16 px) the pitch compresses, to a 28 px minimum, with the newest in front, and slots are clamped so none can lie below the work area: a 40-balloon pile-up stays on screen, the extras sharing the lowest slot and surfacing as the front one is popped. One balloon per session, as with the cards: a repeat completion updates the text and wobbles the existing one. Colours come from a 9-colour palette (red, blue, green, purple, pink, teal, indigo, orange, yellow), picked from an FNV-1a hash of the session id and skipping colours already on screen. Calls are marshalled to the UI thread as before; there is no "dismiss all" (not requested).
- `TrayViewModel.ShowDemoBalloonsCommand` + a **Preview notifications** tray-menu item fire three generic sample notifications 500 ms apart through the same `INotificationService`, so the look can be seen — and verified end to end — without waiting for a real session to finish.
- Tried and removed because they didn't help: `BitmapCache` on the balloon (blurred the hover text), swaying via the window's `Left` (no benefit over the render transform; moving a layered window costs about as much as re-rendering it), per-window software rendering (noisy, no consistent gain).

**Verified**:
- A real-window harness linking the production `NotificationService` and `BalloonWindow` unchanged — real windows, real mouse input, a probe window behind them — 38 checks, all passing on the final code. A balloon starts just above the taskbar and rises monotonically, in the predicted time (within 300 ms), to the top-right slot, hugging the right edge with the window's x constant and fully opaque on arrival. Four balloons take slots top-down in arrival order with four distinct colours. Real hover enlarges only the balloon under the cursor (1.06× vs ≤1.01× for its neighbour). A real click on a transparent margin reached a probe window behind the balloons and popped nothing. A real click on a balloon pops only that one, the others float up to close the gap, the click doesn't reach the window behind and the foreground window is unchanged — focus isn't stolen (extended style `TOOLWINDOW | NOACTIVATE | TOPMOST | LAYERED`). A repeat completion refreshes in place: no duplicate, text updated, a visible wobble, position unchanged. 13 balloons compress to a 57.8 px pitch with the last ending exactly on the bottom inset; popping one from the middle re-flows the rest; a notification raised from a thread-pool thread is marshalled; 40 at once all stay inside the work area and pop one by one. CPU is about 33–41% of one core for ~2.6 s while four balloons rise and 0.00% with four resting; closing with balloons still open is clean.
- Leak soak, 90 balloons through the production service (quick pops, full rises and in-place refreshes mixed): kernel handles 450 → 452, GDI 9 → 9, USER 16 → 15, private memory flat (115.7 → 112.6 MB), no balloon window left alive.
- The real compiled app, driven through its real tray context menu with UI Automation and real mouse input: **Preview notifications** produced three 268×312 balloons at x = 1428, y = 14 / 186 / 358 (screenshots show glossy teal, indigo and orange balloons gathered at the top right with legible text); a five-frame capture shows a balloon rising from over the taskbar to its slot with a sway; a real click on the middle one popped only it (foreground window unchanged) and the last one floated up into the gap; the rest popped cleanly. Four more preview/pop cycles held the real process's handle count (603–611), GDI (30) and USER (33–35) flat. **Quit from the tray menu with three balloons on screen** ended the process within 10 s, leaving nothing behind and no error/warning events in the Application log — the first live graceful Quit (earlier follow-ups listed it as not verified live).
- Build 0 warnings; all 69 tests pass.
- **Not verified**: a real "session finished" balloon in the live app with this UI — only through the Preview item and the harness (the `TrayViewModel` → `INotificationService` call is unchanged from the version that was seen with real data). The layout assumes a bottom taskbar (the only placement Windows 11 supports), since balloons launch from the work area's bottom edge.

#### Follow-up (2026-10-09, later): summary-logic review — bugs fixed, decisions left open

**Requested by the user**: "can you review all the summary logic in this app? I'm thinking about how to improve the summaries for each session", then "explain the things that I need to decide and fix the obvious bugs".

**Method**: the code was read end to end, then checked against the real `session-store.db` (850 sessions), the `events.jsonl` files and the `workspace.yaml` files — counts and lengths only, no session content copied anywhere.

**Bugs fixed** (each test-first; the new end-to-end launcher tests fail on the old code with exactly the corruption seen in a real session):
- **Launch scripts corrupted non-ASCII text.** `CopilotTerminalLauncher` writes the `.cmd` as UTF-8 without a BOM, but cmd.exe reads it in the console's OEM code page (850 on this machine), so `—` reached `copilot` as `ÔÇö`. The app's own provenance header contains an em dash, so *every* "Resume with summary" seed was affected (both real seeded sessions in the database contain `ÔÇö`), as was any accented/CJK text in "Start new task". The script now starts with `chcp 65001 >nul`. A cmd-only stub cannot detect this (`echo` round-trips the mangled text back through the same code page); the test therefore runs the generated script from a CP850 console against a stub `copilot.cmd` that hands its arguments to a real PowerShell child process.
- **`%` was replaced by `_`** (`100%` → `100_`, `%APPDATA%` → `_APPDATA_`). `SanitizeForCmdExe` now doubles it (`%%`, the batch-file escape).
- **Resume seed from the turns recap** (the tier used by 406 of 850 sessions, since only 28 have a checkpoint overview): (1) the 4000-character cut was applied to "Originally asked" + "Most recent outcome" together, so a long first message pushed the outcome out entirely — the outcome survived for only 159 of 388 eligible sessions; each part now has its own budget (1200 / 2400 characters). (2) Only the *last* turn's reply was read, but 20% of interactive sessions end on an empty reply (autopilot sessions finish through `task_complete`, not a message); the last non-empty reply is used. (3) The first message of a session that was itself seeded by this feature is that feature's own header plus body, which was quoted as the "original ask"; the header is stripped (also in its flattened/`ÔÇö` form from older seeds). (4) Truncation could split a surrogate pair (an emoji at the boundary), and a multi-line title put a line break in the header; both fixed. The composition moved from private code in `TrayViewModel` into `Core.Services.ResumeSummaryBuilder` (plus `SessionText`) so it can be unit-tested; tier order and header text are unchanged.
- **Titles with line breaks** (270 stored summaries contain one; WPF `TextBlock` doubles the row height — measured 31.9 vs 16.0 px — even with trimming on) are now collapsed to one line, capped at 200 characters, for row titles and the notification body.
- **Elapsed labels were frozen at load time.** Live rows are built once; later events only patched `Status`, so "Idle · 7h 42m" stayed for a session that had really been idle 17h 25m, and a state change did not restart the clock. `SessionItemViewModel` now tracks when the current state began; the monitor tick refreshes live rows and a status change resets to 0s.
- **Duplicate rows at startup** (found while verifying the above): `StartMonitoring` fired the live-list load and the first poll concurrently, and both add a row per open session after awaiting, with no check at insertion time. Rows are now added only if the session isn't already listed, and the first poll waits for the initial load.

**Verified**:
- 104 tests pass (69 existing + 35 new), build 0 warnings. Mutation check: reintroducing each old bug (last-turn-only, per-part budget removed, header strip disabled, surrogate guard removed) fails exactly the tests written for it.
- Through the **real npm `copilot.cmd` shim** (copied) and real Node, from a CP850 console, the real generated script delivered intact: non-ASCII incl. emoji, `%`/`%1`/`%~dp0`/`%%x`, cmd metacharacters (`& | > < ^ ( ) !`) and quotes.
- Old vs new resume seeds over all 850 real sessions: checkpoint-tier output identical for all 28; "Most recent outcome" present for 388 of 388 eligible recap sessions (was 159); longest launch line 4,065 characters (was 4,409; cmd's limit is 8,191); every one of the 850 seeds contains non-ASCII, i.e. every one was hit by the encoding bug. No real session currently has the nested-header case (both seeded sessions have checkpoints), so that fix rests on its unit tests.
- Elapsed labels: harness on the production `SessionItemViewModel` (14 checks), then the **real app** via UI Automation — labels now match the registry (17h 47m → 17h 48m a minute later, "Working · 30m" matching the true 30m) where the previous build showed the frozen load-time values.
- Startup race: the real `TrayViewModel` driven with fake dependencies of tunable latency — 39 of 40 timing scenarios ended with 2–3 duplicate rows in the wrong order before the fix, 0 of 40 after. In the real app it showed up once (the first launch after a rebuild: 14 rows instead of 13) and not in 8 later restarts.
- Real-app Summary panels (4 rows, real data): names single-line, header intact, recap with latest outcome for 3 of 4, checkpoint overview for the other.

**Not fixed in that pass — needed a decision** (explained to the user; all five were then decided and implemented, see the next follow-up):
- **Where a session's title comes from.** The database `summary` is a truncated prompt for 60% of sessions that have one, differs from the user's own `/rename` in 11 of 17 renamed sessions, and goes stale (this very session is still titled by its first topic). `workspace.yaml` has `name` + `user_named`, and the latest checkpoint has a ~43-character AI-written `title`; using them means depending on an undocumented file format. Related: whether the app's own local rename stays as an override on top.
- **Whether resume seeds keep their paragraphs.** They are flattened to one line because the text sits on a cmd command line. Keeping them means writing the seed to a UTF-8 file and launching through PowerShell (`pwsh` 7.6 is installed here) — a different launch mechanism that needs testing against the real `copilot`.
- **What the seed contains.** Only the checkpoint `overview` (median 531 characters) is used; `next_steps` (median ~1 KB) and `work_done` (~2 KB) are ignored. overview + next_steps is median 1.5 KB / p90 2.8 KB (fits the 4000 budget); adding `work_done` is median 3.5 KB / p90 5.7 KB. Also whether to add a "just acknowledge and wait" line, since `-i` makes the new agent act on the seed immediately.
- **What balloons say.** Title is repo/cwd (63% of sessions share theirs; 90% have no repository) and body is the static session name. A real outcome (`session.task_complete.summary` or the last assistant message) exists for 96% of turns, but `LatestEvent` is `session.usage_checkpoint` in 70% of them, so the engine would have to extract the text from the whole polled chunk; it also puts assistant text on screen and may want a minimum-duration threshold. `ElapsedWorking` is still always null.
- **Resume chains.** A session created by "Resume with summary" looks like any other in the list. Its first message starts with a header containing the original id, so provenance can be derived from the database without local state — a badge, collapsing, or doing nothing is a UX choice.

#### Follow-up (2026-10-09, later): the five summary-logic decisions, implemented

**Requested by the user**: "go on with your picks" — each of the five open decisions above was implemented the way it had been recommended.

**1. Titles.** `SessionSummaryProvider.ResolveTitle` picks, in order: the name from the session's `workspace.yaml` when `user_named` is true (a `/rename`), the latest non-empty checkpoint title, the database `summary`, then a non-user-named workspace name only if it is a single line. This app's own local rename still wins over all of them. `SessionWorkspaceReader` is a read-only, defensive mini-YAML reader because the file format is undocumented and 274 of the 564 real files store the name as a block scalar: plain, single- and double-quoted scalars (escapes and folding) and `|` / `>` block scalars, a 256K-character cap, opened with `FileShare.ReadWrite | Delete`, and an unsafe session id yields nothing. A failure reading the file, the checkpoints or the turns falls back to the next source instead of failing the row.

**2. Seeds stay one line** — deliberately unchanged: no PowerShell-file launch.

**3. Seed content.** A checkpoint seed is now the overview (≤1500 characters) plus `Next steps: …` (≤2300); with no next steps the overview may use the whole 4000. The turns recap keeps its 1200 / 2400 budgets. The header now ends "Please acknowledge in one short line and wait for my next message.]", because `-i` makes the new agent act on the seed immediately. `SessionHistoryStore.GetCheckpointsAsync` reads `next_steps` and falls back to the old query when the column doesn't exist.

**4. Balloons.** The detection engine now keeps per-session turn state (`SessionTurnTracker`) across polls, so the outcome no longer depends on `LatestEvent` (usually `session.usage_checkpoint`). A real user message (a `user.message` without `source`) resets the outcome and starts the clock; the summary of `session.task_complete` wins (or `Blocked: <reason>` when its outcome is `blocked`), otherwise the last non-empty main-agent `assistant.message` (sub-agent messages carry `parentToolCallId`); an `abort` clears it. `Finished` events carry `Outcome` and `WorkedFor` (last event minus the last real user message). The balloon title is the session's resolved name, the body is the first lines of the outcome as plain text (`SessionText.ToPlainSnippet`: code fences dropped; headings, bullets, quotes, links, `**` and backticks stripped; ≤240 characters), the hint reads "Worked 12m · Click to pop", and a turn under 30 seconds raises no balloon. The body gets three lines when the title fits on one line and two otherwise.

**5. Resume chains.** `ResumeSummaryBuilder.FindOriginSessionId` reads the original id from the provenance header in a session's first message, and rows show ` · ↪ resumed from "<title>"` (title ≤60 characters) on the detail line when it resolves in the database.

**Verified**:
- Build 0 warnings; 204 tests pass (104 before).
- `SessionWorkspaceReader` against PyYAML as an oracle over all 564 real `workspace.yaml` files: 0 mismatches (469 with names, 17 `user_named`).
- `SessionSummaryProvider` over all 850 real sessions: all 17 `/rename`s applied; 27 titles come from a checkpoint title; 38 titles differ from the database's; all 28 checkpoint seeds carry Next steps; the acknowledge line is in all 850 seeds; the longest launch line is 4,132 characters (cmd's limit is 8,191); both seeded sessions resolve their origin in the database.
- Four real seeds delivered byte-intact through the real npm `copilot.cmd` shim and Node from a CP850 console.
- A real-pipeline harness (real detection engine, event reader, history store, workspace reader, provider, `TrayViewModel`, `NotificationService` and `BalloonWindow` over fixture files): 15 of 15 checks; its screenshot shows a one-line title with a three-line body, a two-line title keeping two lines, and the hints fitting.
- Mutation check on a scratch copy of the new code: 48 single-point mutations (turn tracker 14, engine hand-off 1, text helpers 7, title provider 6, seed builder 8, workspace reader 12) applied one at a time. 37 were caught by the existing tests; 11 survived and showed real gaps — the duration measured from the first instead of the latest user message, a negative duration when events arrive out of order, an earlier turn's last reply surviving a new user message, null/empty `source` and `parentToolCallId` values, a closed session's outcome surviving a reopen, the exact one-minute duration boundary, the `\n` escape, path traversal through a session id, a BOM before the first key, CRLF inside block scalars, and nested keys listed after the real ones. 16 tests were added (188 → 204), all 11 are now caught, and the original code passes every one.
- The real app via UI Automation: this session's row is titled by its checkpoint title, its detail line carries `↪ resumed from "…"`, and its Summary panel header ends with the acknowledge line, contains "Next steps: " and keeps the em dash. The elapsed labels advance (Working 25m → 27m, Idle 18h 54m → 18h 55m over about 90 s) and the row count is 4 before and after, i.e. no duplicates; after the final relaunch the tray tooltip reads "13 session(s) · 1 working".

**Not verified**: a real "session finished" balloon with the new title and outcome in the live app (only through the harness); a real `copilot -i` launch with the new seed (the seed bytes were verified through the real shim instead, so no real agent was started); the workspace reader's 256K-character cap (no test).

**Still open**: multi-line seeds (pick 2 was deliberately left alone); the first poll still reads each live `events.jsonl` in full from offset 0 (live files up to 12.8 MB here, 57 MB at most across all sessions), and a tail-only read would have to keep the latest real user message in range because the worked duration is measured from it; clicking the tray icon while the popup is open re-opens it instead of closing it.

#### Follow-up (2026-10-09, later): the summary panel shows a summary a person can read

**Requested by the user**: "how to make the checkpoint summary more human-readable in this app?" — asked as a question; the user was not available to choose between options, so the recommended one was built (autopilot) and left uncommitted.

**What was wrong, measured on the real app and the 850 real sessions**:
- The panel showed the text sent to Copilot, not a reader's view: a fixed 360×320 window whose visible ~7 lines were all the 396-character `[Context: … /resume … acknowledge …]` header; this session's real content (2,604 of 3,000 characters) was below the fold.
- Raw markdown: this session's text showed 6 `**` markers and 14 `-` bullet lines. Real checkpoints: the overview is one paragraph in 96% (median 536 characters, max 1,066); next steps are bullet lists in 89%, with inline code in 89% and numbered items in 43%.
- Only 28 of 850 sessions have a checkpoint (sources over all sessions: checkpoint 28, recap 406, title only 37, nothing 379), but the label always said "from Copilot CLI's own checkpoints". The recap tier is the common one: its first request has a median of 4,777 characters (55% cut at the 1,200 budget) and its latest reply is markdown with code fences in 53%, bold in 52%, inline code in 82%, headings in 17% and tables in 8%.
- Frozen at load: this session's checkpoint 2 was written at 12:20, after the app started at 12:18, and only appeared after a restart.

**Design**: separate what a person reads from what the model is sent. The seed text is unchanged; the panel renders the same content without the header.
- Core: `SessionSummaryView` (`SessionSummarySource` ordered None < TitleOnly < Recap < Checkpoint, labelled `SessionSummarySection`s, checkpoint number and time, `Describe(now)` such as "Checkpoint 2 · 38m ago"). `ResumeSummaryBuilder` now composes parts once (`Compose`) and renders them both as the seed (`RenderSeed`) and as the view (`ToView`), so the sections hold exactly the text and limits the seed uses; `SessionSummaryDetails.View` carries it.
- `SummaryMarkdown` (with `MarkdownBlock`): a small forgiving reader — paragraphs with their line breaks, headings, bulleted/numbered lists with nesting (to depth 6), fenced code, tables as monospace rows without the separator row, `**bold**`, `*italic*` and `` `code` `` with CommonMark-style flanking rules, and everything it doesn't recognise (links, HTML, underscores, `***`) left as literal text. Linear-time by construction: the next backtick run of equal length is precomputed, and runs are built in a buffer (a first version merged runs by string concatenation, which the linear-time test showed to be quadratic).
- App: `SummaryDocumentBuilder` turns the blocks into a `FlowDocument` shown in a read-only, selectable `RichTextBox`. `SessionSummaryWindow` is now resizable (580×580, minimum 440×400, clamped to the work area), with the source label, section headings, and the exact seed behind a collapsed "Text sent to Copilot when starting a new session from this summary" expander with a Copy button — so the original session's id stays one click away.
- Freshness: `SessionItemViewModel.Summary` and `SummaryView` are updatable and `TrayViewModel.RefreshSummaryAsync` re-reads them (and the row title, unless there is a local rename) when the panel opens and before "Resume with summary". A re-read that yields a less informative source than the row already has (e.g. a briefly locked database) is ignored.

**Decisions made without asking**: the panel is the human view (not just plain-text flattening); italics are rendered because 93 real passages use them; an AI-written plain-English digest is not included (it would cost a Copilot request per session and send the text to the model); no "start from this summary" button was added to the panel (the row's resume menu already has it).

**Verified**:
- Build 0 warnings; 303 tests pass (204 before): `SummaryMarkdownTests` (formatting, edge cases, pathological and 2,000-deep inputs), `SessionSummaryViewTests`, and view/provider tests added to the builder and provider test classes.
- Seeds unchanged: `ResumeText`, title and origin hashed for all 850 real sessions before and after the refactor — 849 identical; the one difference is a session whose newest turn was recorded eight seconds after the baseline capture, and its seed shrank by exactly the 217 characters its last reply did. In the real app the seed shown in the panel hashes identically to the old code's seed for this session, and Copy puts exactly those 3,000 characters on the clipboard.
- Parser over every real summary section (887 sections, 876,814 characters): 0 exceptions, slowest parse 13 ms, and 0 word-sequence mismatches against an independent word-level oracle (the first run's 2 "mismatches" were the oracle splitting `` `X`s `` into two words). It found 93 real `*italic*` uses, which led to italics support. After that, 10 runs across all sections still show literal markers: nearly all are bold or code spans cut off by the 1,200/2,400-character truncation, and one is a `**` glob in plain text.
- Mutation check on a scratch copy: 44 single-point mutations of the parser, the view and its label, the builder and the provider. 41 were caught at once; 3 survived and showed real gaps (an indented line after a blank line joining the list item above it; `**` markers needing a non-space neighbour on their inner side, opening and closing), each now covered; a mutation without the nesting-depth limit crashes the test host with a stack overflow, i.e. is caught.
- A WPF harness driving the real `TrayViewModel`, `SessionSummaryWindow` and `AppStateStore` against a fake store whose data changes between calls: 25 of 25 checks — "Resume with summary" launches with the newer checkpoint's seed, the row and title follow it, a local rename survives, a failed re-read changes nothing, demo rows trigger no reads, and the panel opens on the refreshed content (and for demo, empty and title-only rows).
- The real app via UI Automation and screenshots: all 4 rows' panels open in 0.3–1 s including the re-read, the header is hidden, no raw markers remain, labels are accurate ("Checkpoint 2 · 1h 38m ago", "No checkpoint yet · recap of the first request and the latest reply" or "…of the first request"); the expander is absent from the UIA tree while collapsed, expands to the exact seed; resizing to 760×640 re-lays out the text (captured with `PrintWindow` — `CopyFromScreen` showed stale pixels outside the old size) and shrinking stops at 440×400; the dialog sits flush with the work area's bottom edge instead of running under the taskbar; the tray tooltip still reads 13 sessions.

**Not verified**: a checkpoint written while the real app is running followed by opening the panel there (the real `.copilot` database is read-only to this app; covered by the fake-store harness instead); high-DPI scaling and a second monitor; screen-reader behaviour beyond UI Automation's text pattern.

**Still open**: a bold or code span cut by truncation shows its literal `**` or backtick at the cut (about 10 runs across the 887 real sections); tables are monospace rows rather than a grid; links, images, HTML and `_underscore_` emphasis are shown as typed.

#### Follow-up (2026-10-16): rows no longer keep a session-id name

**Requested by the user**: "can you check why [the tray] sessions listed are mostly named with sessionId" — asked as a question; the cause was found, and the fix built and left uncommitted.

**Cause**, reproduced with a harness that drives the real `TrayViewModel` and `SessionDetectionEngine` over fixture files, and checked against the real `.copilot` data:
- A row's name was resolved once, when the row was built. The monitor builds the row within one 3-second poll of a session's lock file and registry entry appearing, but Copilot CLI writes the name (`name` in `workspace.yaml`, and the database `summary`) only when the first prompt is processed — 6 seconds after the session started in the measured case, any time later for an interactive one. So every session started while the tray was running got the `Session xxxxxxxx…` stand-in and kept it until the app was restarted; later events only patched the status. A freshly started app looked fine (1 of 13 real rows id-named) and the list degraded over the day, with the newest rows, which are inserted at the top, hit first.
- A session that never gets a prompt never gets a name: 103 of the 590 real `workspace.yaml` files have none, 102 of them with almost no events. The repository/folder fallbacks came only from the database row, which a new or empty session does not have, although every `workspace.yaml` records `cwd` (590 of 590; `repository` in 89).
- Same cause, second symptom: a row built before the database row existed had no working directory, so Resume opened the terminal in the home folder instead of the session's own.

**Fix**:
- `SessionWorkspaceReader` / `SessionWorkspaceInfo` also read `cwd` and `repository`; `SessionSummaryDetails` gains `Repository` and `Cwd` (the history row first, `workspace.yaml` second). An open row's name is now the title, else the repository, else the folder, else `Session <id>…`, and its detail line and working directory use the same folder.
- `SessionItemViewModel.HasPlaceholderName` marks a row whose name is a stand-in. On every monitor tick `TrayViewModel.RefreshPlaceholderNamesAsync` re-resolves those rows' titles (`GetTitleAsync`, which never reads turns), keeps trying while the session is open, makes one last try after it closes, and stops once a title is found.
- The user's own name always wins: a local rename clears the flag synchronously, and no row is updated while the Summary dialog is open — it edits `DisplayName` two-way, so a background change there would be mistaken for the user's rename and saved as a local override.

**Decisions made without asking**: the stand-in is the folder (what a database-backed row already falls back to), not a generic "New session"; a nameless open row costs one small file read and two read-only SQLite queries every 3 seconds for as long as it stays open and nameless (measured on the real database: about 0.5 ms per lookup, 0.017% of a core).

**Verified**:
- Build 0 warnings; 316 tests pass (303 before). The 13 new ones cover the reader (folder and repository in every scalar form, empty values, nested keys) and the provider (history row wins, workspace fallback, blank values, nothing known). Mutation check on a scratch copy: 4 single-point mutations (fallback removed, precedence reversed, blank values counted as present, `cwd` no longer read) — all caught.
- `SessionWorkspaceReader` against PyYAML over all 590 real `workspace.yaml` files: 0 mismatches for `cwd` and `repository`.
- The harness (the real `TrayViewModel` and `SessionDetectionEngine` over fixture files, with the real Summary window driven through its visual tree): the old code failed 6 of the 9 checks that apply to it (a new session's row is id-named, has no folder, and stays id-named after the name is written; an empty session is id-named; Resume launches with no folder; a session named and closed within one poll stays id-named); the new code passes all 18. That includes the dialog cases: a name typed while Copilot's name arrives is kept and saved as the local override, and a name arriving while the dialog is merely open is applied after it closes and is not saved as an override. Mutation check on the view model: removing the flag clear on a local rename fails 1 check (the typed name is overwritten), removing the dialog guard fails 5 (the name changes under the open dialog and is saved as an override).
- Real `.copilot` data, read-only, through the real `TrayViewModel` with a temporary state file: 13 rows before and after, id-named rows 1 → 0 (the open session that never had a prompt now shows its folder, with the right working directory).
- The tray was restarted on the fixed build through its scheduled task, and its tooltip reads 13 sessions, as the read-only run predicted.

**Not verified**: the row text in the live popup (it is hidden, so only the tray tooltip was read through UI Automation).

**Still open**: the detail line's "opened … ago" is computed once when the row is built, so it never advances, and reads "opened just now ago" for a row built in a session's first minute; the database `summary` can be a raw first prompt (two real fork sessions show one) and outranks a better generated workspace name.

### 9.8 Feature (2026-10-16): desktop stickers for pinned sessions

**Requested by the user**: stickers on the screen, from the top-left corner, that only show data taken from the sessions (nothing to edit by hand, so it stays cheap); a pin button for each session in the panel; an X to hide a sticker; a double-click to resume the session with its history after a confirmation dialog; resizable; each one shows the gist of its session and time information; green when the session is working and light yellow otherwise; the transparency adjustable in the panel. The user asked for the remaining design decisions to be put to them before any building.

**Decisions, asked first and built as answered**:
- The X *snoozes*: the sticker hides until that session's next state change, then comes back. The pin stays; a sticker is only removed by unpinning in the popup. Snoozes are not saved across restarts.
- Stickers are always on top and never take focus (`WS_EX_NOACTIVATE`, tool-window style, no taskbar button).
- A double-click asks "Resume … with its full history in a new terminal?" (Yes/No, default No) and then runs the existing "Resume with history". A session that is still open is treated the same as a closed one.
- Bookmark style: any row, open or closed, can be pinned, and its sticker stays (as Closed) when the session ends. The tray menu has a "Show stickers" master switch.
- The gist is the session's title plus what its latest turn reported — or, while it works, the request it is working on. The time line reads "state · how long in it · opened … ago".
- Stickers stack down the left edge from the top-left corner and wrap into a second column; they can be dragged and resized, and each one's place and size is remembered.
- One transparency slider in the popup's footer (20–100 %, default 90 %) fades whole stickers, text included.

**Decisions made without asking**: primary monitor only; removing a row from the list unpins it; demo rows cannot be pinned; pinning while stickers are hidden turns them back on; a remembered position that is no longer on any screen (a changed monitor layout) is discarded and the sticker is placed afresh.

**Implementation**:
- `Core`: `StickerState` (the in-memory copy of the settings, pins and placements; writes through `IAppStateStore` 400 ms after the last change, on a `TimeProvider`, and flushes on dispose), `StickerLayout` (first-fit placement in device-independent pixels: 8 px margin and gap, 240×124 default, 170×88 minimum, an off-screen test), `SessionGist` (which text to show, flattened to at most 240 characters), the small models, and `AppStateStore` keeping a `stickers` block in `app-state.json` (cleaned on read, so a hand-edited file cannot break the app).
- The detection engine's `SessionChangeEvent` gains `Request` (the user message the turn is answering, at most 2000 characters, replaced by each real user message), and `Started`/`Finished` events now carry `Outcome` too, so a sticker for a session that was already open has its text from the first poll.
- `App`: `StickerWindow` (borderless, transparent, topmost, never activated; `DragMove` to move, three resize thumbs, the X, double-click) and `StickerManager` (keeps one window per pinned row in step with the list; owns the snooze map and the confirmation). `SessionItemViewModel` gains the bindings (`IsWorking`, `IsPinned`, `Gist`, `TimeLine`, …), `TrayViewModel` the pin command and the visibility and opacity state, and it now also loads pinned sessions that fell off the ten most recent closed ones. A related bug turned up while building this and was fixed: a Closed row kept saying Closed after its session was resumed, so a pinned sticker would have stayed a yellow "Closed"; the row now becomes a live one again.

**A deadlock found while verifying, and fixed**: `StickerState.Dispose()` — called on exit through the container, on the UI thread — waited for its final write with `FlushAsync().GetAwaiter().GetResult()`. When the file write inside the store happened to finish before its `await` was reached, the rest of the write ran on the UI thread, and `AppStateStore` ends its writes with an `await using` that does not opt out of the captured context, so its continuation was queued to the very thread that was blocked waiting; the process hung. The harness that loads the real popup hit it on roughly half of its runs (a stack dump showed the UI thread parked in `Dispose` and nothing else running); in the real app it would have hung the process at Quit whenever a sticker change was still inside its 400 ms quiet period. `Dispose` now runs the flush on the thread pool, where no such context exists, and waits at most 3 seconds, so a write that cannot finish is dropped rather than hanging the exit. A new test has the store resume on a context that is never pumped; it fails against the old code, and 10 of 10 popup runs now exit normally.

**Verified**:
- Build 0 warnings; 383 tests pass (316 before). The 67 new ones cover the layout geometry, the state (a fake clock and a fake store for the quiet period, the real store for a restart), the gist rules, sticker persistence including a hand-edited file, and the engine's `Request`/`Outcome` on `Started`, `WorkingStateChanged` and `Finished`. Mutation check on a scratch copy: 25 single-point mutations across those areas (slot spacing and margin, column wrap, off-screen test, size limits, gist choice and fallback, recap versus checkpoint text, pin bookkeeping, opacity clamping, placement cleaning, flush on dispose (also the old blocking version), the quiet period, request replacement and cap, which events carry what); all caught except changing the 240-character limit itself, which its test deliberately reads from the constant. One mutation (the previous quiet-period timer no longer cancelled) first survived because a "nothing written yet" assertion could run before the write on the thread pool; those two assertions now wait a moment first, and that mutation fails 3 runs out of 3.
- A harness driving the real `TrayViewModel`, `SessionDetectionEngine`, `StickerManager` and real sticker windows over fixture files passes 47 checks: gists come from the events history (a working row shows its request, an idle one its last outcome, a closed one its recap); stickers stack from the top-left corner without overlapping; green for working, light yellow otherwise; text and time lines; topmost and non-activating window styles; the slider reaches every sticker and is saved; the time line advances; the X hides a sticker until its session's next state change and then it returns; the resize thumbs stop at the minimum and the size is remembered; a closed session that is resumed becomes a live row and its snoozed sticker comes back; the master switch, unpin and re-pin; after a restart every pin, the opacity and each moved sticker's place return (including a pin older than the ten recent closed rows) with no overlaps; a double-click asks first — No launches nothing, Yes resumes a closed session in its own folder, and an open session is treated the same.
- The real `MainWindow.xaml`, constructed (not shown, so no second tray icon) over a fixture view model, passes 14 checks: the opacity slider takes its range and value from the view model and moves it back, a percentage label follows, every row has a pin button that — invoked through UI Automation — pins the row, creates a sticker with the slider's opacity and turns into "Unpin", and the tray menu's "Show stickers" item is checkable, follows the view model and hides every sticker when unchecked. The app's own container (`App.ConfigureServices`, validated on build) hands out one shared view model, state and manager and starts the manager (5 checks).
- Rendered images of the stickers, for fixture and for real data: long titles wrap to two lines and long gists end in "…" at the default and the minimum size.
- Real `.copilot` data, read-only, through the real view model with a temporary state file: five real sessions pinned; the working one is green and shows its current request, the others are light yellow with their latest outcome.
- CI's own commands (restore, Release build of the solution, Core tests) pass locally.

**Not verified**: real mouse input — dragging a sticker, dragging a resize edge, a real double-click and a real click on the X. (Verified since, with the real mouse: see the follow-up below.) The harness has a real-mouse pass, but when it ran, the session's input desktop was the screen saver (the machine had been idle for 20 minutes), where `SendInput` is denied, so the pass reports a skip instead of a result. The double-click path was checked by raising the mouse-down event with `ClickCount = 2` (raw `WM_LBUTTONDBLCLK` messages sent to the window do not give WPF a click count, which is why the first attempt failed); the drag uses `Window.DragMove()` on the first click and resizing uses the standard `Thumb` pattern, both usual WPF idioms but not exercised with a physical mouse here. Also not tried: a second monitor, mixed DPI, and what Copilot CLI does when a session that is still open elsewhere is resumed (the user chose to treat open and closed sessions alike).

**Still open**: a session with no prompt yet has no gist, so its sticker shows only the title and the time line; stickers live on the primary monitor only.

#### Follow-up (2026-10-16, later): the text scales with the sticker

**Requested by the user**: "when I resize it, can you also make the fontsize scale along?"

**Decision made without asking — how much**: the title (13 px), the gist (11.5 px) and the time line (10 px) are multiplied by the harmonic mean of the sticker's growth in width and in height against the default size (240×124), never below 0.85 and with no upper limit. That is exactly 1 at the default size and exactly the ratio for a proportional resize (2 at 480×248, 3 at 720×372); when only one side changes it lands between the two ratios but leans toward the tighter one (480×124 or 240×248 gives 1.33), so a very wide or very tall sticker cannot grow its text until the gist is squeezed out. At the minimum size (170×88) the text is at its floor: 11 / 9.8 / 8.5 px. The ✕, the padding and the resize handles keep their size. Considered and rejected: following only the smaller side (the sticker the user had actually resized, 249×186, would have grown its text by 4%), and the geometric mean (a wide, short sticker loses its gist).

**Implementation**: `StickerLayout.FontScale` (Core, in the tests) and `StickerWindow.OnSizeChanged`, which sets the three font sizes on every size change, so the text follows a drag live and a sticker restored after a restart comes back at the right size. The title is capped at two lines by a `MaxHeight`; a fixed 36 px no longer fits once the font changes, so the cap is now two line heights.

**A bug the scaling exposed, and its fix**: with fractional font sizes the long title often rendered one trimmed line followed by a blank one (5 of 8 test sizes). Reading WPF's `TextBlock` source showed why: a wrapped line is skipped when the block's layout-rounded height is below the sum of the lines' fractional heights (two 21.19 px lines need 42.38 px but the block is rounded to 42 px), while the space for both is still reserved. At 13 px two lines are 34.58 px and round up to 35, which is why it never showed before. The title now sets its own `LineHeight` (with `BlockLineHeight` stacking) to a whole number of device pixels, so two lines always add up exactly, and `MaxHeight` is exactly two of them.

**Verified**:
- 405 tests pass (383 before; the 22 new ones cover the exact values, the floor, unusable sizes such as NaN, zero and infinity, that growing a sticker in either direction never shrinks its text, and the bound for lopsided stickers). Mutation check on a scratch copy: 9 mutations of the rule (tighter side only, arithmetic mean, geometric mean, a wrong baseline for the height, no floor, a different floor, and the three guards); all caught.
- The harness drives the real sticker window: the exact font sizes at eight window sizes (default, 249×186, 1.5×, 2×, 3×, wider only, taller only, the minimum), through the real grip and edge handles, and after a restart; the long title shows two rendered lines (counted from the rendered pixels, since the box height alone could not tell) at every one of 84 sizes of a sweep, and a short title stays one line high; rendered images at those sizes were looked at, with the ellipsis on the last line of both the title and the gist.
- The real-mouse pass ran at last (the desktop was unlocked): a real drag moves a sticker and the position is remembered, a real grip drag resizes it and the text follows, a real double-click asks for confirmation, a real click on the ✕ hides it. It also showed the sticker never takes keyboard focus: Windows' foreground window stayed the same through a click, a drag and a resize, although WPF's `IsActive` flips to true (the earlier check looked at `IsActive`, which is the wrong measure, and now looks at the foreground window).

**Still open**: the ✕ stays small next to the large text of a big sticker; a sticker enlarged in one direction only shows less of its gist, because its text grew without the box growing with it.

#### Follow-up (2026-10-16, later still): screenshots in the README

**Requested by the user**: "in the current readme.md, there's no images to show users what this app got, can you add some images there to make the app easier to grasp".

**What was added**: a "What it looks like" section in the README and seven images under `docs/images/` — an overview (stickers, popup and balloons on a desktop), the popup, the Summary panel, the stickers (one enlarged, which shows the text scaling), three balloons, the tray icon idle and with its glow, and the tray menu. The repository is public, so the images show only made-up sessions (`Refactor retry policy`, `C:\Work\sample-repo`, `acme/widget-api`, …): no real session name, folder or user name appears, and the files carry nothing beyond the standard PNG chunks. The blue backdrop and the three labels on the overview are the only parts drawn by hand; the windows themselves are the real ones.

**How they were made**: a scratch harness, kept outside the repository, builds the real windows over fixture files and renders them. Each window's content is a `RenderTargetBitmap` of its real visual tree. The Windows title bar and frame, which WPF does not draw, come from `PrintWindow`, called by a throwaway Scheduled Task, because the shell this work ran in is inside a job object that denies screen capture, and a WPF window's client area comes out blank through `PrintWindow` anyway; the frame is cropped to its visible bounds, rounded and outlined like Windows 11 (the captured frame's own border is black). Only the harness's own windows were captured, never the desktop. One thing worth knowing for anyone repeating this: showing a window that contains the `TaskbarIcon` makes `H.NotifyIcon` create a real tray icon (its `Loaded` handler calls `ForceCreate`), so the harness removes that element from the popup before showing it.

**Seen while capturing, not changed**: in "Start a new Copilot task" the label of the "Enable all permissions" check box is cut off at the right edge at the window's default width, and the history window still describes itself as a "first cut"; neither window is in the README.

**Still open**: the images are not regenerated automatically, so one that shows a window whose look has since changed has to be redone by hand.

#### Follow-up (2026-10-20): Pin keeps a session at the top of the list; stickers get their own button

**Requested by the user**: "Can we use the pin button to pin the session in the panel, and a separate button to pin it on my screen as sticker?"

**Vocabulary change**: *pin* now means what it says in the panel, a row that stays at the top of the list, and the note on the desktop is a *sticker* with its own button ("🖥 Add sticker" / "🖥 Remove sticker"). §9.8 and its follow-ups above were written before this and use "pin" and "pinned" for what is now a sticker. In the code, `SessionItemViewModel.IsPinned` is the panel pin and `HasSticker` the desktop note; Core keeps its sticker names (`StickerState.IsPinned`, `SetPinned`, `PinnedSticker`, the `stickers.pinned` block of `app-state.json`) so saved files stay readable.

**Decisions, asked first and built as answered**:
- Pinned rows float to the top of the list and always stay in it, however old the session is.
- A session that already has a sticker becomes a panel pin too, once, the first time the new build starts.
- The popup keeps its 360 px width, and the buttons wrap onto two lines (Resume, Summary and Remove above; Pin and the sticker button below).

**Decisions made without asking**: a pin and a sticker are independent, neither implies the other; "✕ Remove" clears both, and its confirmation says so for a row that has either; a pinned row shows a small 📌 beside its name; pinned rows keep the list's usual order among themselves (open sessions first, then closed ones by recency, then pinned sessions that fell out of the list, newest first) instead of the order they were pinned in; a pinned or stickered row does not use up one of the ten closed-session slots, so a pinned session inside the ten makes an eleventh row; demo rows cannot be pinned.

**Implementation**:
- `Core`: `IAppStateStore` gains `GetPinnedSessionIdsAsync` (null when nothing was ever saved) and `SavePinnedSessionIdsAsync`, and `AppStateStore` keeps a `pinnedSessions` list in `app-state.json` (blank and duplicate ids are dropped on read). `SessionPins` is the in-memory list. When the file has no `pinnedSessions` yet it starts from the sticker ids and saves at once, so the migration happens only once, even if there were no stickers. It ignores a change that changes nothing and serializes its writes, so concurrent saves end with the latest list; an I/O failure keeps the pin in memory, and any other error is not swallowed.
- `App`: `TrayViewModel.Pins.cs` holds `SessionsView` (a `ListCollectionView` over the sessions, sorted live by a comparer: pinned first, ties by position in the underlying list, resorted when a row's `IsPinned` changes) and the Pin command; the old pin command became `ToggleSticker`. Rows added later get their saved pin and sticker flags, so a pinned session that opens again goes to the top, and the loader brings back sessions that are pinned or have a sticker and fell off the ten most recent. `MainWindow.xaml` binds to `SessionsView`, stretches the rows so the button columns line up, and has the two button lines.

**Seen while verifying**:
- A comparer that returns 0 for two rows with the same pin state does not keep their order, so the tie is broken by position in the list; a mutation without that tie-break is caught.
- `ListBox` rows are only as wide as their content unless `HorizontalContentAlignment` is `Stretch`; with the shorter button lines the buttons stopped lining up. The first version of the row-width check measured the border of the `ListBoxItem` template instead of the row's own, so it passed whatever the layout did; it now finds the row border by its padding.
- Live sorting moves a row at most about one dispatcher tick after the change. Of 404 random toggles one needed a wait before the order was right, at worst 15.7 ms, so nobody sees it, but a check has to wait for the order to settle.
- The view model shows three placeholder demo rows until the live load finishes, so a check that waits for three rows can run against them; the harness now waits for the live flag and the expected ids. A probe of 60 fresh view models loaded live every time (median 110 ms, slowest 336 ms).

**Verified**:
- Build 0 warnings; 426 tests pass (405 before). The 21 new ones are in `AppStateStoreTests` (never saved reads as null, a file written before pins existed, the round trip in order, an empty saved list is not "never saved", pins and stickers do not overwrite each other, a hand-edited file is cleaned) and `SessionPinsTests` (the migration, and that it is not inherited again even when everything was unpinned; set and unset order; idempotence; blank ids; `Ids` is a copy; concurrent saves ending with the latest state; an I/O failure keeping the pin; another failure not being swallowed; the migration through the real store). Mutation check on scratch copies: 12 single-point mutations in Core, all caught. It first found two gaps, closed with the "everything unpinned stays empty" and "other errors are not swallowed" tests, and the in-flight-save test was made stricter.
- A harness mode driving the real `TrayViewModel`, `SessionDetectionEngine` and `MainWindow` over fixture files passes 26 scenarios: pinned rows float and keep their order; 400 random pin toggles agree with a simple model; a new session lands below the pins; pins survive a restart; a pinned closed session does not use a slot; old pinned sessions come back newest first; pin and sticker are independent; "✕ Remove" clears both; the demo-data round trip; the popup's layout (rows as wide as the list, button columns aligned) and the buttons invoked through UI Automation; the migration from stickers. Mutation check on the app: 11 single-point mutations (reversed sort, no tie-break, the closed-slot quota, old pins in the wrong order, no migration, Remove leaving the pin, rows not stretched, a sticker also pinning, a pin also making a sticker, new rows not given their saved pin, live sorting off), all caught, with the unmutated baseline passing.
- The older harness modes still pass after the renames: 51 checks (stickers without the mouse), 14 (the popup), 5 (the container), 28 (font scaling).
- CI's own commands (restore, Release build of the solution, Core tests) pass locally.
- The README's popup and overview images were regenerated from the real windows with made-up sessions; the other five are unchanged.

**On the user's own machine afterwards**: the first thing the user saw was the old popup, with the sticker session in its ordinary place and no pin order. The cause was not the code: the machine had been restarted, the tray had been started at logon by its Scheduled Task, which runs the `bin\Debug` program exactly as it was built, and that build was still the one from before this change (only a Release build had been made while the old tray held the Debug files). Rebuilding Debug in place and restarting fixed it. The check against real data then passed: before the restart, the real view model over the real sessions and a copy of the real `app-state.json` migrated both sticker sessions to pins, listed them first, saved `pinnedSessions` and left the `stickers`, `preferences`, `readMarkers` and `customDisplayNames` blocks unchanged; after it, the real `app-state.json` held the same two ids with the other blocks unchanged, both stickers reappeared where they had been, and the real popup, read through UI Automation, listed the two pinned rows first (each with "📌 Unpin" and "🖥 Remove sticker") and the other rows after them (with "📌 Pin" and "🖥 Add sticker").

**Not verified**: real mouse clicks on the two new buttons (UI Automation invoked them; the real-mouse pass was not re-run); high DPI.

**Still open**: a pin for a session that has since been deleted from Copilot's history stays in the saved list, unseen; a row removed with "✕ Remove" cannot be pinned again until it comes back on its own; the two-line buttons make rows 107–118 px high, so the popup shows about two and a half rows at once.

#### Follow-up (2026-10-20, later still): a sticker closed with ✕ reads "Show sticker" in the panel

**Reported by the user**, with a screenshot of the popup: "I already closed the second sticker". That row's button still read "🖥 Remove sticker".

**Cause**: not what ✕ does, but what the panel said about it. ✕ snoozes a sticker (§9.8: hidden until its session next changes status, not saved across restarts) and the sticker stays registered, so the row's `HasSticker` stayed true and its button kept offering to remove a note that was no longer on the screen. Before the Pin/Sticker split the same button read "Unpin", which fitted "the pin stays"; the new wording made the mismatch visible. On the running tray: one sticker window visible, both ids still in `stickers.pinned` of `app-state.json`.

**Decision made without asking**: keep ✕ as the user chose it (hide until the session next changes, which is what brings a finished session's sticker back) and make the panel say so, rather than making ✕ remove the sticker. A hidden sticker's button reads "🖥 Show sticker", with a tooltip saying it was closed with ✕ and returns when the session next changes; a click shows it at once, and turns the stickers back on if they were all switched off. Removing a hidden sticker therefore takes two clicks, Show and then Remove. Considered and rejected: making ✕ remove the sticker (it would drop the return-on-change the user chose) and calling the hidden state "Add sticker" (the sticker still exists and still comes back by itself, so the label would be untrue).

**Implementation**: `SessionItemViewModel.IsStickerHidden` drives `StickerButtonText` and a new `StickerButtonToolTip`, which `MainWindow.xaml` binds. `StickerManager.Snooze` sets it; it is cleared when the status changes, when the user clicks Show, and whenever the row loses its sticker. `ToggleSticker` has a third case for a hidden sticker, and `StickerManager` re-syncs on the flag and drops a snooze whose flag the view model cleared.

**Verified**:
- Build 0 warnings; 426 tests pass (no Core change).
- A harness mode driving the real `TrayViewModel`, `StickerManager`, sticker windows and `MainWindow` passes 30 checks: X on a sticker leaves the row saying "Show sticker" (with its tooltip) while the saved sticker stays; one click shows it and the row offers Remove; Show then Remove removes it, and adding it again gives a shown sticker rather than a stale hidden one; a working session's sticker stays hidden until the session finishes, then returns and the row follows; Show also switches the stickers on when they were all off; a row that loses its sticker is never hidden, and "✕ Remove" on a row with a hidden sticker clears everything; the real popup's button text and tooltip follow the row and its button brings the sticker back; after a restart a hidden sticker is shown again. Mutation check on scratch copies: 10 single-point mutations (the label ignoring the flag, Show removing instead of showing, X not setting the flag, the flag not cleared on a status change, not cleared when the sticker goes, Show not switching the stickers on, the snooze surviving a cleared flag, the manager not listening to the flag, the tooltip not bound, Show not clearing the flag), all caught.
- The older harness modes still pass: 59 checks (the pin scenarios), 14 (popup), 51 (stickers without the mouse), 5 (container), 28 (font scaling).
- On the user's machine: the tray was restarted through the Start-menu script, both stickers came back at their saved places, and the second was closed again so the screen matched what the user had; the real popup, read through UI Automation, shows "Remove sticker" for the shown sticker, "Show sticker" with the explaining tooltip for the hidden one, and "Add sticker" for rows without one. The saved `app-state.json` was unchanged by all of this.

**Not verified**: a real mouse click on ✕ and on the new button (the harness raises the click events and UI Automation invoked the buttons).

**Still open**: a sticker hidden with ✕ is shown again after a restart or reboot, as §9.8 decided, so the restart that deployed this change brought the second sticker back until it was closed again; saving the hidden state would need a way to tell whether the session changed while the tray was not running.

#### Follow-up (2026-10-27): the lists no longer scroll sideways

**Reported by the user**, with a screenshot of the popup scrolled to the right: "Can you improve this UI so I don't need to scroll right all the time?" The Resume, Summary, Remove, Pin and sticker buttons were off the right edge and the names were cut at the left.

**Cause**: one long name. A name taken from the session is cut at 200 characters (`TrayViewModel.MaxDisplayTextLength`), which is still far wider than the popup, and a session whose first prompt was a pasted "summary of an earlier session" is named by it. A `ListBox` whose horizontal bar is on `Auto` gives each row unlimited width, so every row became as wide as the longest line. The name's `TextTrimming="CharacterEllipsis"` was already there but could not take effect, because the text was never given less room than it needed, and the right-aligned buttons sat at the far end of the row. Reproduced in the real `MainWindow` over fixture sessions: the list's extent was 1103 px in a 309 px viewport, with the buttons at x ≈ 900–1092. The history window has the same list and the same fault (1085 px in 361).

**Fix**, in both `MainWindow.xaml` and `SessionHistoryWindow.xaml`:
- `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on the list. Rows are measured at the viewport's width, so the name ends in "…" as intended, the detail line wraps (a path with no break point wraps inside the word) and the buttons stay in view.
- `ScrollViewer.VerticalScrollBarVisibility="Visible"`, for the reason below.
- A tooltip on the name. It turned out to hold only the name as already cut at 200 characters, on one unwrapped line; see the follow-up below.

**Why the vertical bar is always shown, found while verifying**: with only the first change the content could still be nudged sideways. A `ScrollViewer` whose vertical bar is `Auto` first measures its content as if there were no bar (326 px instead of 309), and a pass like that ran while scrolling; a trace of the panel's measure calls caught it on scrolling to the third row of five. `VirtualizingStackPanel` kept the 325 px extent it computed there after the bar came back, so `ScrollableWidth` read 16 px and `ScrollToHorizontalOffset` moved the first row 16 px to the left, which is what a horizontal gesture or a key would do to the list. With the bar always shown that pass never happens: the trace has no 326 px measure and the extent stays 307.7. It also stops the text re-wrapping when the bar appears and disappears. The cost is a faint, disabled bar beside an empty list.

**Decisions made without asking**: a long name is cut to one line, with the tooltip for the rest, rather than wrapped over several lines, which would make rows that are already 107–118 px high taller; the history window got the same change; the vertical bar stays on, as above.

**Verified**:
- Build 0 warnings; 426 tests pass (no Core change).
- A harness shows the real `MainWindow` and the real history window (off-screen, so nothing flashes on the desktop) over fixture files: five sessions, with names of 200, 122 (an address with no spaces), 96 and 23 characters, and a 159-character folder path with no break point. It scrolls to every row and checks that each row, button and text ends inside the viewport and that the name has a tooltip, then checks that the list has nothing to scroll sideways and tries to nudge it. With both XAML files put back as they were for the check, the popup fails 4 of its 5 checks (extent 1103 px in a 309 px viewport) and the history window 3 of its 4; with them, everything passes: extent 307.7 px, and the right-most button ends at 298 px of 309.
- The same check over the real `.copilot` data (read-only, with a temporary state file): about a dozen rows, the longest name 103 characters; nothing scrolls sideways.
- The older harness modes still pass: 14 checks (popup), 59 (pin scenarios), 30 (hidden stickers), 5 (container), 51 (stickers without the mouse).
- Rendered images of the popup and the history window with long names, and of the popup with no sessions, were looked at.
- On the user's machine: the tray was restarted through the Start-menu script (Debug build in 22 s, started through its Scheduled Task); the new process is responsive, the three stickers are back on the screen and the Application event log has no errors for it. The live popup was not opened (it would take the keyboard focus), so what the user sees there rests on the harness.

**Seen while verifying**: an early version of the check laid the window's content out by hand, as the older harness modes do, and reported buttons clipped by the scroll bar that the shown window does not have; the check now uses a shown window. Checking each row once, when first seen, missed the stale extent described above; it now looks at every row while scrolling.

**Not verified**: a real touchpad swipe, Right-arrow press or wheel (the nudge was tried through `ScrollToHorizontalOffset`); high DPI.

#### Follow-up (2026-10-27, later): hovering a clipped title shows the whole of it

**Reported by the user**: "The titles are clipped, can you make sure when my cursor hovers, I can see the full info?"

**What was wrong**: the tooltip added in the entry above was bound to `DisplayName`, which the view model has already cut to 200 characters, so a hover showed the same cut text ending in "…", on a single unwrapped line (1055 px wide for 200 characters, because a WPF tooltip does not wrap by itself), after WPF's default one-second delay. That change's check only asserted that a tooltip was set, never what a hover shows. Other clipped text had nothing at all: a sticker's title (two lines, then "…") had no tooltip, its gist's tooltip was one 1285 px line, and the `↪ resumed from "…"` title on the detail line is cut to 60 characters with nothing to reveal the rest. Measured on the real database, lengths only: Copilot keeps the first prompt as the session `summary`, at most 503 characters (median 502), and 257 of 502 sessions are over 200, so about half of all titles were cut; checkpoint titles are at most 63 and `workspace.yaml` names at most 511.

**Fix**:
- `SessionItemViewModel` keeps the whole title next to the cut one: `SetFullTitle(full, shownAs)` and `NameToolTip`, which is the whole title while `DisplayName` is still the cut form of it and `DisplayName` itself otherwise, so a local rename wins. `DetailToolTip` holds the detail line with the whole origin title, set only when that line was shortened.
- `TrayViewModel` flattens the whole title to at most 1000 characters (`MaxFullTitleLength`) and applies it wherever a title is resolved: the two row builders, the summary refresh and the placeholder-name refresh. `AppendOriginAsync` returns the short and the whole detail line. The 200-character cut itself stays, since it also feeds the confirmation dialogs, the Summary panel's name box and the balloons.
- Popup and history window: the title and the detail line carry a tooltip that wraps at 440 px and opens after 400 ms (the WPF default is 1000 ms). The detail line's tooltip exists only when the line was shortened (`ToolTipService.IsEnabled`).
- Stickers: one tooltip on the whole card shows the title, the gist and the time line, wrapped. It replaces the gist-only tooltip, and the ✕ keeps its own.

**Decisions made without asking**: the whole title lives in a separate property instead of raising the 200 limit (the other consumers above would all have grown); the cap is 1000 characters, comfortably over the 503 seen; the tooltip is 440 px wide with a 400 ms delay; `ShowDuration` is left alone, since in .NET 10 its default is already `Int32.MaxValue` (read in dotnet/wpf), so a tooltip no longer vanishes after five seconds; a title's tooltip is always there, also for a short title that is not cut, because whether a `TextBlock` is trimmed is not known to the view model; the gist keeps its own 240-character cap (`SessionGist.MaxLength`, in Core), so a sticker's tooltip shows the gist as capped; the card's tooltip also appears over a resize handle (pressing a mouse button dismisses it).

**Verified**:
- Build 0 warnings; 426 tests pass (no Core change).
- A harness shows the real windows over fixture files with titles of 503, 400, 300 and 150 characters. Before the fix a hover showed 200 of 503 characters on one 1055 px line (the history window the same), the sticker's title had no tooltip and its gist's was one 1285 px line. After it: the whole 503 characters, wrapped into seven lines at 440 px, opening after 0.4 s; the `resumed from` tooltip with its 445 characters in six lines; no tooltip at all on a line with nothing cut; on a sticker, title, gist and time line together (12 lines, 440 × 213) from the title, the gist or the time line, while the ✕ keeps "Hide until this session changes state". A check that no text block in a tooltip is cut off compares the lines drawn with the lines the text needs. A title that arrives after the row was built (a folder-named row gaining its title) updates the hover, a rename wins over it, and the property notifies.
- Physical mouse, once the screen was unlocked: the history window (title, resumed-from line and the no-tooltip control), the sticker (title, gist, time line and ✕) and, in the popup, the 150-character title, the resumed-from line and the control all pass. The popup sits at the screen's bottom-right corner and its tooltips are shifted left to stay on the screen (`1260 to 1680` of 1680, `1241 to 1680`). The popup's 503-character title could not be hovered physically in that run: the cursor was somewhere else on three attempts (the user's own mouse), so it rests on the emulated trigger below and on the identical tooltip in the history window, which passed physically.
- Emulated trigger: Windows' lock screen covered the desktop for part of the work, so a physical-mouse pass could not run. WPF starts a tooltip from `PopupControlService.OnMouseMove(directlyOver)`, which has no window-activation requirement (read in dotnet/wpf), so the harness hit-tests the real window at the title's position and hands that element to the same method: owner lookup, delay, content and size then go through WPF's own code. Posting `WM_MOUSEMOVE` to the window did not start tooltips (not even the ✕ button's), so that route was dropped.
- Mutation check on the app, with the emulated trigger so the mouse was left alone: 5 single-point mutations, all caught: a title arriving later not updating the hover; a rename not winning over the whole title; the detail tooltip without its guard (an empty bubble appears on a row with nothing cut); the history window's tooltip bound to the cut name; the sticker tooltip's title not wrapping. The last one first survived, because the checks compared text and a total line count and the gist's lines hid it; the cut-off check above now catches it. The popup's tooltip bound to the cut name is the "before" run.
- The older harness modes still pass: 14 checks (popup), 59 (pin scenarios), 30 (hidden stickers), 5 (container), 51 (stickers without the mouse), 28 (font scaling), and the layout checks of the entry above (5, 4 and 5).
- Rendered images of the title, resumed-from and sticker tooltips were looked at.
- On the user's machine: the tray was restarted through the Start-menu script (Debug build in 53 s, started through its Scheduled Task) and stayed responsive. With the real cursor over each of the three real stickers (no clicks, so no focus change), the tooltip window, read back through UI Automation, appeared 0.6 to 0.7 s after the cursor arrived, 433 to 434 px wide, holding the title, the gist and the time line as separate text blocks (lengths 103, 240 and 34; 47, 192 and 33; the 103-character title shown in full). A first probe that waited a fixed 1.5 s found the tooltip only on its second try for the two stickers it looked at; the timed probe that followed saw it on the first hover for all three, which I put down to the probe's first look at the process through UI Automation rather than to the app, though I did not prove that.

**Seen while verifying**: the popup hides itself when it loses activation, and a harness process cannot take the foreground from another program, so the harness shows it without activating it; the first window a process shows is hidden by Windows' start-up rule when the shell that started the process asked for a hidden window (`Window.IsVisible` said true while Win32 said hidden), so the harness forces it visible. Showing the real popup also creates a real tray icon (`H.NotifyIcon`'s `Loaded` handler), so the harness removes that element first.

**Not verified**: the live popup of the running tray (opening it would take the keyboard focus), high DPI, a second monitor, and a tooltip reached with the keyboard.

**Still open**: a sticker's gist is cut at 240 characters in Core, so its tooltip can still end in "…"; the Summary panel remains the place to read everything.

#### Follow-up (2026-10-29): the tray no longer fails to start when it is launched at logon before Explorer

**Reported by the user**, with a screenshot of the error dialog: "When I restart my PC, the app failed to restart".

**Cause**, from Task Scheduler's operational log: the PC booted at 15:02:13 and the user logged on at 15:04:14; the logon trigger started `CopilotSessionTray.App.exe` at 15:04:15.07 and `explorer.exe` itself started at 15:04:16, so there was no taskbar yet when `MainWindow.InitializeTrayIcon()` asked for the icon. `Shell_NotifyIcon` failed and `H.NotifyIcon`'s `TrayIcon.Create` threw `InvalidOperationException: TryCreate failed.` (the stack in the screenshot). The exception left `App.OnStartup`, which skipped everything after the icon: its first render, `StickerManager.Start()` and `StartMonitoring()`. The dispatcher's error handler showed its dialog and the process stayed alive holding the single-instance mutex, with no monitor, no stickers and no icon (a second launch would only have said "already running — check your system tray icon"). It stayed like that for 33 minutes, until the restart script stopped it (15:37:12) and started a working instance (15:37:33). It is a race, so some logons work: the previous reboot, on 10-20, started fine. `H.NotifyIcon` re-creates its icon when Explorer announces a new taskbar (`TaskbarCreated`, which its hidden top-level window does receive) but swallows any failure there, and nothing retried the first creation.

**Fix**:
- `TrayIconStarter` (App) creates the icon and, while that fails with an `InvalidOperationException` or `Win32Exception`, retries on a dispatcher timer: every second for 30 attempts, then every 5 seconds, until the icon exists or `Stop()` is called. If there is still no icon after 5 minutes the failure is raised once through the dispatcher (the existing error dialog) and the retrying carries on. The report is posted as its own dispatcher operation: the first version rethrew inside the timer's tick, and a `DispatcherTimer` is not re-armed when its handler throws, so the retrying stopped after the report; the isolated check on the starter caught it.
- `MainWindow.InitializeTrayIcon` renders the icon first (assigning `TrayIcon.Icon` before the icon exists only stores the handle, which `Create` then uses) and starts the starter in a `finally`; `ShutdownTrayIcon` stops it.
- `App.OnStartup` starts the sticker manager and the monitor in a `finally`, so nothing in the icon's setup can skip them.

**Decisions made without asking**: retrying in the app rather than delaying the Scheduled Task's logon trigger (that task already exists on the user's machine and was registered by the app, and a delay would not help a slow shell at any other moment); keeping `H.NotifyIcon`'s own `TaskbarCreated` handling and letting it coexist with the retry; no log file (the app has none), so the one-time dialog after five minutes is the only way a persistent failure shows.

**Verified**:
- Reproduced before the fix. A scratch harness starts itself as a child process on a Windows desktop made with `CreateDesktop`, where no Explorer runs, and runs the real `MainWindow` and `H.NotifyIcon` there (`SetThreadDesktop` cannot be used in-process: the STA main thread already owns a hidden COM window and gets `ERROR_BUSY`), with a small fake `Shell_TrayWnd` that can appear later and answers the shell's `WM_COPYDATA` messages. Nothing reaches the user's own desktop or tray. On the unfixed code `InitializeTrayIcon` throws `InvalidOperationException: TryCreate failed.` with the screenshot's stack (`TrayIcon.cs:332`, `TaskbarIcon.ForceCreate`, `MainWindow.InitializeTrayIcon`), the icon image is never rendered, and the harness's next scenario dies on the same exception.
- With the fix, 28 checks pass: `InitializeTrayIcon` returns, the icon is not created yet, its image is already rendered, nothing is raised while waiting; when the taskbar appears the icon is created within about 0.5 s, once (the library's DELETE, ADD, SETVERSION, MODIFY), later updates reach it and shutdown removes it; with a `TaskbarCreated` broadcast it is created within about 0.1 s and ends up created with at most one extra add (the library removes and re-adds); shutting down while still waiting stops the retries; and `TrayIconStarter` on its own: the first attempt is immediate and its failure does not escape, retries are fast and then slower, nothing is reported before the limit, one report follows as the original exception and the retrying carries on, it stops after the first success and on `Stop`, an unexpected exception is not swallowed, a `Win32Exception` is retried.
- Mutation check on scratch copies: 9 single-point mutations, all caught (no retry; not stopping after a success; reporting at once; reporting by throwing inside the tick, the defect above; `Stop` doing nothing; swallowing every exception; retrying only `InvalidOperationException`; shutdown not stopping the retries; the icon not rendered before creating it).
- Build 0 warnings; 439 tests pass; the older harness modes still pass (pins 59, popup 14, stickers without the mouse 51, container 5, font scaling 28).
- On the user's machine: the tray was restarted through the Start-menu script (which also rebuilt `bin\Debug`, the program the logon task starts, so the next logon runs this build); it is responding, its icon is in the notification area (read through UI Automation), the saved state was unchanged, and the real popup lists the three pins first.

**Not verified**: a real logon (the PC was not rebooted: the harness reproduces the missing taskbar, not Explorer's real timing); `App.OnStartup`'s new `finally` (the real `App` cannot run beside the user's own tray because of the single-instance mutex; it is a small change by inspection).

**Still open**: if Explorer announces the taskbar and the library's re-add fails once (it swallows the error), the icon stays missing until the next announcement, as before; a periodic check that the icon still exists would cover it. `AppStateStore` writes `app-state.json` in place (`File.Create`, then serialize), so a power loss or kill in the middle of a write could leave it empty, and an empty file reads as no state at all; writing a temporary file and replacing the old one would remove that.

#### Follow-up (2026-10-29, later): a sticker closed with ✕ stays closed across restarts

**Reported by the user**, in the same message: "Also I notice some stickers automatically showed up when the app restarts, I don't remember turn on those stickers".

**What the evidence shows**: a sticker is only ever created for a session listed under `stickers.pinned` in `app-state.json`. Checked rather than assumed: the real view model and sticker manager over the real sessions, with copies of the user's state, show 0 sticker windows for the state at that moment (three pins, no stickers) and exactly two for the state of 10-20 (two stickers, two pins); a panel pin never turns into a sticker. The instance that started at logon never reached `StickerManager.Start()` (entry above), so the stickers the user saw came from the instance the restart script started at 15:37:33. `app-state.json` now holds no stickers and was last written at 15:38:58, about a minute later, so what was on screen at 15:37 cannot be read back. The stickers known to have existed were made with 📌 Pin in the days when Pin meant sticker (10-16 and 10-20); one of them had been closed with ✕, which was not saved, so it came back at every start.

**Decision made without asking**: save the ✕. A closed sticker stays hidden until its session's status changes, also across restarts (the "still open" item of 10-20). Considered and rejected: starting every restart with all stickers off (it would undo "stickers survive restarts") and making ✕ remove the sticker (it would drop the return-on-change the user chose).

**Implementation**: `PinnedSticker.HiddenAtStatus` (Core; saved as `hiddenAtStatus`, the status name, in the sticker's entry of `app-state.json`; anything that is not a status name reads as shown), `StickerState.GetHiddenStatus` / `SetHiddenStatus`. `StickerManager` saves the hide on ✕, clears it when the snooze ends (the status changed, the user clicked Show, the sticker was removed) and, when a sticker's window is first created, restores it: still hidden if the row's status equals the saved one, otherwise shown and cleared, so a session that finished, was closed or was resumed while the app was not running shows its sticker again. `Finished` (the unread completion of an idle session) is saved as idle, because after a restart such a row is built as idle.

**Verified**:
- Build 0 warnings; 439 tests pass (426 before; the 13 new ones cover the round trip, the lenient reading, remembering only for stickers, forgetting on removal, keeping the hide across a placement change, writing nothing when nothing changes, and surviving a restart through the real store).
- A harness mode over the real `TrayViewModel`, `SessionDetectionEngine`, `StickerManager`, sticker windows and `MainWindow` passes 21 checks across restarts: a working, an idle and a closed session's stickers closed with ✕ are saved with their status and are still hidden, in the window and in the panel, after one and after two restarts; with the session finished, closed or resumed while the app was down, all three stickers are shown again and nothing is remembered; a hide at Finished is saved as idle and survives a restart; Show forgets the hide, so a restart shows the sticker; removing a hidden sticker and adding it again gives a shown one. The earlier hidden-sticker mode (30 checks) now expects the hide to survive a restart.
- Mutation check on scratch copies: 12 single-point mutations, all caught: 6 in Core (the status not stored, not written, read from numbers, dropped by a placement change, accepted for a missing sticker, written when unchanged) and 6 in the app (the hide not saved on ✕, not restored, not normalized at Finished, not cleared when the snooze ends, a stale hide not cleared on restore, the restore condition inverted).
- The older harness modes still pass (pins 59, popup 14, stickers without the mouse 51, container 5, font scaling 28).
- On the user's machine the tray was restarted onto this build (entry above); there is no sticker to hide in the saved state, so the live hide was not exercised, only what the harness drives.

**Not verified**: a hide through the real mouse and a restart of the real tray (no sticker existed, and adding one to the user's saved settings only to remove it again seemed worse than leaving it to the harness); that the 400 ms write behind a hide survives the restart script's forced stop of the process (the harness flushes explicitly, as the real exit does).

**Still open**: nothing removes the old stickers by itself: the popup's "Remove sticker" does, per row. A row built with a different status than it had when it was hidden (for example a working session whose registry flag lags at start-up) shows its sticker again.

#### Follow-up (2026-10-29, later still): the lists scroll in proportion to the wheel or touchpad

**Reported by the user**: "The scroll of the panel is too sensitive, can it be adjusted?"

**Cause**, measured before anything was changed. Windows' "lines to scroll at a time" setting on the machine is 3 (`HKCU\Control Panel\Desktop\WheelScrollLines`) and it has a precision touchpad. WPF's `ScrollViewer.OnMouseWheel` moves one fixed notch for every wheel message and marks the event handled, whatever its delta (read in dotnet/wpf: it calls `MouseWheelDown()` or `MouseWheelUp()`, and a delta of 0 counts as "up"; `TextBoxBase` does the same). A precision touchpad sends many small events, so a light swipe went as far as a hard flick. The popup's list and the history window's list also scrolled by item (`CanContentScroll` is true by default), so one notch was three whole rows, about 330 px, in a popup that shows about two and a half rows. A harness mode (`scroll`) raises real `PreviewMouseWheel` and `MouseWheel` events on the real windows; on the unchanged code 14 of its checks failed: three rows per event in both lists whatever the delta (an event of -1 moved as far as -120), and the Summary text moved a flat 48 px per event, so eight events of -15 (a notch's worth of touchpad movement) moved it 384 px.

**Fix**:
- `WheelScrollMath.Offset` (Core, so CI covers it): the offset after one event is `offset - delta / 120 × lines × 16`, clamped to the scrollable range, where `lines` is Windows' setting. At 3 lines a notch is 48 px, the distance WPF itself uses for pixel scrolling, and a small event moves a small distance: -15 is 6 px and eight of them make one notch. A negative setting (Windows' "one screen at a time") moves one viewport height per notch. A NaN or infinite offset, viewport or range (a layout that has not happened yet) counts as 0.
- `WheelScrolling.IsProportional` (App) is an opt-in attached property. It hooks `PreviewMouseWheel`, moves the first `ScrollViewer` inside the element by that distance and marks the event handled, so WPF's own handler, which would add a full notch in the bubble phase, does not run. It leaves alone an event that is already handled, any event with a modifier key held (Ctrl-wheel stays WPF's), content with nothing to scroll, and an `ItemsControl` that scrolls by item.
- The popup list and the history list get `ScrollViewer.CanContentScroll="False"` (scrolling in pixels, which a fraction of a row needs) and `IsProportional`. The Summary panel's text, the "text sent to Copilot" box and the new-task window's prompt get `IsProportional`, so every scrolling surface of the app behaves alike.

**Decisions made without asking**: following Windows' setting rather than adding a speed slider to the app (one place to change it, and anyone who has already set it gets what they chose: Settings, Bluetooth & devices, Mouse, "Lines to scroll at a time"); proportional scrolling rather than just a smaller step (a smaller step would still move a light touchpad swipe and a hard flick the same); pixel scrolling for the two lists, which gives up their virtualization, acceptable because they are short (the history window lists at most 30 sessions, the popup the open ones and about ten closed ones: 13 rows with the real data); the same behaviour on the text panels, for one consistent feel; no animation or inertia; Ctrl-wheel and the other modifiers left to WPF; an event of 0 is consumed and moves nothing (WPF's handler would scroll up a notch for it); a list that still scrolls by item is not taken over.

**Verified**:
- Build 0 warnings; 462 tests pass (439 before; the 23 new ones cover a notch at several line settings, small events adding up, an event of 0, the clamps at both ends, page mode, zero lines, and non-finite or nonsense inputs).
- The `scroll` harness mode passes its 47 checks. On the popup list, the history list, the Summary text, the "text sent to Copilot" box and the new-task prompt: a notch moves 48 px, eight events of -15 move 48 px, a notch back up returns to the top and the offset never goes above it, the last position is exactly the bottom, an event of -1 moves 0.4 px, and the preview event is marked handled. On the popup list the first row really moves 48 px on screen, `ScrollIntoView` still reaches the 13th row and the scrollbar's range is the content minus the viewport. An event of 0 moves nothing, and a plain `ListBox` that scrolls by item is left to WPF (3 items per notch, event not handled).
- Mutation check on scratch copies: 26 single-point mutations of the final code, all caught. 12 in the maths (the sign flipped; only the sign of the delta; lines fixed at 3; no page mode; only a lower or only an upper clamp; no sanitising of the offset; the raw range; a range that may be negative; a notch of 100; a line of 20 px; the raw viewport) and 14 in the app (the handler never attached; only the sign of the delta; lines fixed at 1; the event not marked handled; the delta negated; the item-scroll guard disabled; the popup list or the history list in item mode; each of the five hooks removed; an early return for an event of 0 put back).
- The not-marked-handled mutation survived the first pass. The harness raised the preview and the bubble phase back to back and only asked whether the event had been handled in either, and without a layout pass between them WPF's own second scroll could not be seen. The harness now lays out between the phases and asserts that the preview phase consumed the event; the mutation then fails 32 checks (a notch moves 96 px, eight small events 432 px, an event of -1 moves 48.4 px, the preview event is left unhandled).
- The older harness modes still pass on the final tree: 28 (no shell at logon), 14 (popup), 59 (pins), 30 (hidden stickers), 21 (hidden stickers across restarts), 51 (stickers without the mouse), 5 (container) and 28 (font scaling).
- On the user's machine: the tray was restarted through the Start-menu script (Debug rebuilt in 12 s, started through its Scheduled Task). It is responding, its icon is on the taskbar (read through UI Automation), and the saved state is byte-identical. The real popup was opened through its tray icon with UI Automation: one small increment on its list moved it 1.39% of a range of about 1,160 px (16 px, one line), a large increment one page (22.9%, the 265 px viewport), and `SetScrollPercent` took it back to the top; the popup was then closed through its window pattern. With item scrolling a small step would have been about a row, roughly 9% for 13 rows.

**Seen while verifying**: the restarted tray holds 330 to 520 MB for the first minutes (private bytes swing as the garbage collector runs), where the instance it replaced showed 93 MB after more than 16 hours, much of it with the PC asleep (Windows may have trimmed it; not checked). The scroll change is not the cause: a run of the real view model and detection engine with no window at all, over a copy of the state, peaks at 429 MB within four seconds, so it comes from the monitor's start-up on the real data (853 MB of `events.jsonl` in 534 session folders). What in that start-up takes the memory was not looked into.

**Not verified**: a real wheel or touchpad on the live popup (the harness raises the same WPF events; moving the user's cursor onto the popup to scroll it for real was not done), touchpad inertia and how it feels, what the touchpad's own speed setting does to the size of its events, high DPI, and the Summary text, the seed box and the new-task prompt on the real windows (only in the harness).

**Still open**: if it is still too fast or too slow, Windows' "lines to scroll" setting changes it, `LinePixels` (16, in `WheelScrollMath`) changes it for the whole app, and an in-app setting or a momentum scroll could follow. Dragging the scrollbar's thumb and keyboard scrolling are unchanged. The start-up memory above.

#### Follow-up (2026-10-30): a session that needs the user turns the tray icon, its sticker and its row red

**Requested by the user**: "can you add a hook so when a session needs user input, the tray and the sticker become red?" It came after a thread the same day on whether Copilot CLI can say when a session needs input, how its hooks work (a callback the CLI starts with the event as JSON on stdin, not a feed to subscribe to or poll) and whether Claude Code and Codex have hooks too (they do, with different event names and payloads).

**What the evidence shows** (checked on this machine, CLI 1.0.105, before building anything):
- Copilot's registry, `open-sessions-state.json`, holds only `schemaVersion`, `openedAt`, `refreshedAt` and `working` per session, so nothing there says a session is blocked on a prompt. The tray's status (Working, Idle, Finished, Closed) comes from it. (The `TrayAggregateState.WaitingForInput` stub, kept since 2026-10-05 for "a future, real distinguishing signal", is about a different case, an idle session that may or may not expect a reply; it stays unassigned and this feature does not use it.)
- The session's event log does carry it. A permission request is a `permission.requested` event until the `permission.completed` with the same `requestId`; a question is an `ask_user` tool call from its `tool.execution_start` until its `tool.execution_complete`; a plan approval is an `exit_plan_mode` call (one in all the logs, 49 s). The file is written within 0.1 to 0.3 s of each event (file time against the last event's own timestamp, on open sessions). `user_input.requested`, `elicitation.requested`, `exit_plan_mode.requested` and `session.idle` exist but are typed `ephemeral: true` in the SDK's `session-events.d.ts`, so they never reach the file.
- Over all 534 logs (about 200,000 events): 809 permission requests, 807 of them paired with a completion, the other 2 ended by an `abort` a few events later; 170 `ask_user` calls and one `exit_plan_mode` call, all completed. Read as intervals that is 905 prompts (734 permission, 171 question): median 6.7 s, 90th percentile 207 s, 338 shorter than the 3 s poll, 305 of 30 s or longer, the longest 3.5 days (a prompt that was simply abandoned). No log ends with a prompt pending.
- Most of the sub-3 s intervals involve no person. 680 of the 807 permission requests predate the log's `decisionSource` field (it appears from mid-September), and 271 of those were over in under 3 s: instant approvals, mostly in May and June, and denials with nobody asked (`denied-no-approval-rule-and-could-not-request-from-user`). Among the 127 requests that carry the field, 123 are `human_response` (median 27.6 s, 14 under 3 s) and 4 are `unattended_fallback` denials (median 0.6 s). For questions, 24 of the 40 in the 80 most recently modified logs took under 3 s, and the ones with a recorded result say "The user is not available to respond and will review your work later": the CLI answered at once.
- A Copilot hook would say less. The `notification` hook (since 1.0.18; types `permission_prompt` and `elicitation_dialog`) only reports "prompt shown"; there is no "answered" notification, so red could not clear on its own without more hooks on every tool call (each a process start); hook files are read when the CLI starts (the how-to says to start or restart it), so sessions already running would not report; and it needs a new global file in `~/.copilot/hooks` (the folder does not exist here yet). Claude Code, Gemini CLI, Codex and Cursor all have similar hooks (a command, JSON on stdin) with different event names, which is where a provider-neutral interface (Working, NeedsInput, TurnDone, Ended, one adapter per tool) would come in.

**Decision made without being able to ask**: the form offered "event log only (recommended)" or "event log plus a Copilot hook" and the user was not available, so the recommended default was taken: no hook, nothing installed into Copilot; red is driven from the event log the tray already tails. The log is needed for the clearing in every variant, so a hook could only ever be an addition (instant red for sessions started after it was installed, and nothing else), and building it blind would have put a global file in the user's Copilot setup.

**Fix**:
- Core: `PendingPromptTracker` follows one session's events and keeps the open requests (permission requests by `requestId`, `ask_user` and `exit_plan_mode` calls by `toolCallId`, each with its timestamp). An `abort`, `session.shutdown`, `session.resume` or `session.start` drops everything pending (an abort can cancel a prompt without a `permission.completed`, as the 2 unpaired requests show). Only the few relevant event types are parsed (a tool start is first checked for the two tool names as text), so the cost is small: over every event of every log it took under a quarter of a second in total (87 ms in the latest run; the slowest single log, 5,017 events, took 24 ms). `SessionTurnTracker` owns one, `SessionDetectionEngine` reports the result on every transition as `SessionChangeEvent.Prompt` (a `PendingPrompt`: kind `Permission` or `Question`, and since when) and emits the new `SessionChangeKind.NeedsInputChanged` when it changes with nothing else changed. The status stays what the registry and process checks say: red is a layer on top, not a status.
- App: `SessionItemViewModel.PendingPrompt` and `NeedsInput` (false once the row is Closed). A row that needs input says "Needs input" instead of its status, in red, with the red dot instead of the gold glyph, its time counted from when the request was logged (so a prompt left for two hours shows "2h", also after a restart), and a tooltip saying whether it waits for an approval or an answer. `TrayViewModel` applies the prompt on started, working, finished and closed transitions and on `NeedsInputChanged`, exposes `IsAnyNeedingInput` and adds "· N need input" to the tray's tooltip. `TrayIconRenderer.Render` takes an `attention` flag that draws the glyph red (`#E53935`) with a pale red glow; `MainWindow` uses it while any session needs input, pulsing every 1.0 s instead of the working glow's 1.6 s, and the pulse timer runs for either reason. The sticker's card turns `#F28B82` (the trigger comes after the working one, so red wins); the popup row gets a `#FDECEA` tint and a bold status line.
- `StickerManager` keeps a hidden sticker's state as (status, needs input): a sticker hidden with ✕ comes back when its session starts to need input; one hidden while red stays hidden until the status or the prompt changes; a remembered hide is not restored for a row that already needs input.
- README: the popup, stickers and tray-icon paragraphs, a paragraph on how it works and what it cannot see, the layout lines and the test count; `stickers.png` now has a red sticker and `tray.png` a red pair of frames (regenerated offscreen from the sample sessions, like the others).

**Decisions made without asking**: "needs user input" means blocked on a permission prompt or a question for the user, not idle after finishing (the earlier "unread" red dot was removed on request on 2026-10-07; this is a different, urgent signal); the same red for both kinds, told apart in a tooltip; `exit_plan_mode` counts as a question, on one logged use and the SDK's own `exit_plan_mode.requested` event; colour only, no balloon or sound; the list is not re-ordered; the popup's background watermark is not recoloured (the row, tooltip and tray icon carry it); no debounce for a prompt answered within one poll (a poll lands inside a prompt that lasted L seconds with probability about L / 3 s, so the CLI's instant "user not available" answers, about 0.1 s, show red in roughly 3% of cases, and a flash lasts until the next poll, 3 s at most; a delay before turning red would also delay every real prompt); a closed session is never red.

**Verified**:
- Build 0 warnings; 494 tests pass (462 before; the 32 new ones cover the tracker's rules and the engine's transitions).
- The tracker run over all 534 real logs, as above: 905 intervals, none ending pending, 87 ms in total in the latest run.
- A harness mode (`attention`, 72 checks) runs the real view model, engine, sticker windows, `MainWindow` and renderer over fixture logs, reading the tray icon back from the real `TaskbarIcon.Icon`. A permission prompt on a working session turns its row (red text, dot, bold, tint, tooltip, "Needs input" time line), its sticker (`#F28B82`, the others untouched) and the tray icon red (average colour about (232, 71, 67) against about (221, 170, 43) for gold, each moving a few units with the pulse phase) although another session is still working, the tooltip counts it, the icon pulses (centre green about 82 to 167) and answering turns everything back; an `ask_user` call, a plan approval and an abort do the same; a running shell command does not; an idle session shows the red dot instead of the gold glyph and its sticker goes from yellow to red; with two sessions waiting the icon stays red until both are answered; a hidden sticker returns red, one dismissed while red stays hidden until the prompt is answered and then returns green; a closing session stops being red and a reopened one does not inherit its old prompt; a prompt already pending at start-up is red from the first poll, aged from the request ("Needs input · 5m"), also for a sticker that was hidden before the restart, also for a session that opens while the tray runs, and the tray icon is red from the first poll with nothing working; a prompt arriving in the same poll as a working or idle flip is not lost; with nothing working the red icon still pulses and the pulse stops after the answer.
- Rendered images were looked at: the popup rows, the stickers, and the tray icon at 16, 20, 24 and 32 px on a dark and a light background (the glyph stays legible in red at 16 px).
- Mutation check on scratch copies: 50 single-point mutations (25 in Core: the tracker's pairing, clearing, precedence, timing and tool matching, and the engine's carrying and diffing of the prompt; 25 in the app: the row's label, colour, dot, glyph and age, the view model's applying and aggregate, the tray icon's colour, glow and pulse, the sticker's colour, order and hide handling, the popup's tint, weight and tooltip text). 48 were caught at once. Two survived and led to stronger checks: a tool whose arguments merely mention `ask_user` was only tested with an escaped quote that the cheap text pre-filter rejects before the real tool-name check (now also unescaped mentions); and the start-up check used a working session, whose 100 ms pulse repaints the icon from the live property and hid a missing refresh (restarting with everything idle and one prompt pending would have left the icon gold), now an idle one. Both are caught now. One more survives as an equivalent mutant: `NeedsInput` also checks that the row is not Closed, which the Closed transition already guarantees by clearing the prompt, kept as defence in depth.
- The older harness modes still pass: 47 (scroll), 28 (no shell at logon), 14 (popup), 59 (pins), 30 (hidden stickers), 21 (hidden stickers across restarts), 51 (stickers without the mouse), 5 (container) and 28 (font scaling).

**Seen while verifying**: `RecomputeAggregateState` raises `IsAnyWorking` on every call whether or not it changed, and the tray icon's handler repaints on that, so the `IsAnyNeedingInput` notification is not what updates the icon when something is working (the property is read live at each repaint); it matters only for the all-idle case above, where the same call is what repaints.

**Not verified**: a real prompt on a real Copilot session (raising one needs a model session in the user's own Copilot state, which was left alone; the real logs and the fixture logs stand in, in the CLI's real event shapes); the red icon on the real taskbar (it is read back from the real renderer and `TaskbarIcon`, with no shell attached); high DPI and a second monitor; other CLI versions' event names; `exit_plan_mode` beyond its one logged use.

**Still open**: a Copilot `notification` hook as an optional second source (instant red for new sessions, and the first step towards one provider-neutral interface for Claude Code, Gemini CLI, Codex and the like); prompts Copilot does not write to the log (a form an MCP server opens); a balloon and a sound when a session starts to need input (done on 2026-11-01, see the follow-up below); floating those rows to the top of the list; a short grace period if the 3 s flicker for quickly answered requests turns out to matter.

#### Follow-up (2026-11-01): a "Pinned only" checkbox in the popup

**Requested by the user**: "can we have a checkbox in the panel, when checked, only show pinned sessions".

**Decisions made without asking** (none is hard to change):
- **Where**: the popup's header, at the top right on the title's line, so the list keeps its whole height and the subtitle below keeps the full width. Label "📌 Pinned only", with a tooltip.
- **What counts as pinned**: the 📌 pin, which was split from the sticker earlier. A session that only has a sticker is not listed while the box is ticked.
- **It filters the list and nothing else**: `SessionsView`, the `ListCollectionView` that already sorts pinned rows first, gets a filter on `IsPinned` while the box is ticked, plus live filtering on `IsPinned`, so unpinning a row removes it at once and pinning one, from anywhere, lists it. The tray icon, its tooltip, the aggregate state and the stickers keep reading `Sessions`: a hidden session that needs you still turns the tray icon red, counts in the tooltip and keeps its sticker and its alert balloon (see the next entry).
- **An empty list says why**: the popup's empty message used to follow `Sessions.Count == 0`; it now follows the view (`SessionsView.IsEmpty`, re-evaluated on every change of the view, and its text re-read each time). With sessions but none pinned it says "No pinned sessions. Untick “Pinned only” to see all sessions."; with no sessions at all, or in the empty demo scenario, the old messages stay.
- **Remembered across restarts**: `NotificationPreferences.ShowPinnedOnly`, a new last parameter that defaults to false, so state files written before it read as false; saved as `preferences.showPinnedOnly`, in the bag `LastNewTaskWorkspaceDirectory` already uses for panel settings. Saves run one at a time and each writes the value as it is by then, so a quick run of changes ends with the last one saved, and nothing is written when the file already says the same, which is the case when the value has only just been read at start-up.
- **Demo data**: nothing there can be pinned, so a demo scenario with rows shows the "No pinned sessions" text while the box is ticked.

**Verified**:
- 497 Core tests (3 new for the field: both values round-trip, a file written before the field reads false, and saving into such a file keeps every other preference and the pins).
- A harness mode `pinnedonly` (79 checks, scenarios 59-68) runs the real view model, engine, sticker manager and `MainWindow`: ticking lists just the pinned rows in their usual order at once and unticking restores all of them with no filter left; unpinning and pinning (also of a hidden row) change the list live, the longest wait for the view being 12.6 ms (57 ms in an earlier run); a session that starts meanwhile is counted but not listed until pinned; the four empty states (no sessions, none pinned, last pinned row unpinned or removed, demo data) and the popup's own text following them as the session count crosses zero; the value saved as `showPinnedOnly`, still there and already applied after a restart, never rewritten when it was only read, and every other preference kept either way round; the tray icon, tooltip, aggregate state and stickers unaffected, including a hidden session turning the tray icon red; the real popup's checkbox (one only, its label and accessible name, top right on the title's line, no taller header, two-way bound, ticked through UI Automation); and 250 random pins, unpins, ticks and removals each ending with the right list, the right message and the last saved value.
- Rendered images were looked at: the popup unticked, ticked and empty, and the regenerated `popup.png` and `overview.png` (the header now shows the checkbox).
- The deployed tray (restarted onto this build with the Start-menu script, PID responding afterwards): its real popup has the "📌 Pinned only" checkbox at the top right, unticked; ticking it through UI Automation narrowed 14 rows to the 3 pinned ones and unticking brought all 14 back, with the process responsive throughout; the saved state gained only `showPinnedOnly: false` and every other value in the file was unchanged.
- Mutation check on scratch copies: 20 single-point mutations (the filter's direction, setting and removal, live filtering and its property, the empty state's source, subscription, text and notification, saving, the saved value, the equality guard and loading, the checkbox's binding and place, the tooltip and aggregate leaking the filter, the two defaults). All 20 are killed. Two led to changes in the checks: removing the empty message's property notification survived at first, because the view model's getter is always right when read directly and only a bound text can be stale, so the popup's own text is now checked as the session count crosses zero; and removing the save was only caught by a JSON helper throwing on `"preferences": null`, which now returns "missing" so a check fails instead.

**Not verified**: a real mouse click on the real popup's checkbox (UI Automation's toggle was used in the deployed tray), high DPI and a second monitor.

**Still open**: a count of the hidden sessions, or of hidden ones that need you, next to the checkbox; a tray-menu entry for the same setting.

#### Follow-up (2026-11-01, later): a red balloon and a sound when a session needs you

**Requested by the user**: "Could we have a desktop notification with a sound when user input is needed?" The 2026-10-30 entry had listed exactly this as still open.

**What it does**: when a session starts waiting for a permission prompt or a question (the same waits that turn the tray icon red), a red balloon floats up the right edge like the finished-turn ones, titled with the session's name and saying "Needs your approval" or "Needs your answer", and Windows' notification sound plays. The balloon pops by itself when the wait is over (answered, aborted, or the session closed) as well as when it is clicked.

**Decisions made without asking** (none is hard to change):
- **"Desktop notification" is the existing balloon**, not a Windows toast: the app's notifications are these balloons, and a toast would need an app identity (a registered AppUserModelID or packaging) and would pass through Focus Assist, for no gain. The balloon is a window of its own, kept apart from a session's finished-turn balloon (the service keys balloons by session and kind), so one session can show both.
- **The sound is `Notification.Default`**, played with `PlaySound(alias, SND_ALIAS | SND_ASYNC | SND_NODEFAULT | SND_SYSTEM)`: the sound the user's Windows scheme has for notifications (here "Windows Notify System Generic.wav"), through the System Sounds volume, so it follows their scheme and can be silenced the usual way. `SystemSounds.Exclamation` would have been simpler, but on this machine the system exclamation and asterisk events are both "Windows Background.wav", a faint sound; it is the fallback where the notification event is not defined. At most one sound every two seconds, so several sessions asking in one poll ring once.
- **One alert per wait** (`NeedsInputAlerts`, in Core, fed every change the detection engine emits): alert when a session goes from not waiting to waiting; a second request while one is pending, or the prompt's age or kind changing, is the same wait; answering, an abort, a resume or the session closing ends it and takes the balloon down. Waits that began before monitoring did (an old unanswered prompt, replayed from the log on the first poll) are remembered but never alerted, so starting or restarting the app does not ring for old prompts; a prompt with no timestamp is taken as raised when it was observed.
- **No grace period**: the alert comes with the poll that sees the wait (3 s at most). Human-answered permission prompts are rare in the logs (123 since the log began recording who decided, in mid-September), and the one kind of instant prompt, the CLI's own "the user is not available" answer to `ask_user` (24 of the 40 questions in the 80 most recent logs), is caught by a poll only about 3 % of the time, so a grace period would mostly delay real alerts.
- **Mute notifications silences both** the balloon and the sound (checked fresh each time, like the finished-turn balloons); taking an alert down is never muted. Quiet hours are still not enforced anywhere.
- **Red is reserved**: the finished-turn palette lost its red (`#E53935`, the alert's and the tray icon's), so it has 8 colors and red only ever means "needs you". The README's second sample balloon changed from red to purple for that reason.
- **Preview notifications** now ends with the alert instead of the old "Blocked: needs your approval…" finished-turn sample, so the look and the sound can be tried without a real prompt.
- **Independent of the popup**: it runs for every change whatever the popup shows (demo data, "Pinned only"), like the finished-turn notifications.

**What changed**:
- Core: `NeedsInputAction` and `NeedsInputAlerts`; `INotificationService` gained `NotifySessionNeedsInputAsync` and `DismissNeedsInputAsync`.
- App: `IAlertSound` and `WindowsAlertSound` (registered in the container); `NotificationService` takes an `IAlertSound` and a `TimeProvider` (for the two-second gap) and shows, refreshes, throttles and pops alert balloons; `BalloonWindow.IsAlert`; `TrayViewModel` holds the `NeedsInputAlerts`, runs each change through it in the poll loop, resolves the title with the helper it now shares with the finished-turn notification, and swallows any notifier failure so it can never break the poll.
- README: the popup, balloons and wait paragraphs, the layout lines, the test count; `balloons.png`, `overview.png` and `popup.png` regenerated.

**Verified**:
- 23 new Core tests for `NeedsInputAlerts`: every kind of change carrying a wait, both kinds of wait, the boundary at the moment monitoring began, old waits remembered and answered, repeats, kind changes, answering once, a new wait after an answer, closing, a reopened session, independent sessions and a prompt without a timestamp.
- A harness mode `alerts` (57 checks, scenarios 69-75): through the real view model and engine over fixture logs, a permission request, an `ask_user` question and a plan approval each raise one alert with the session's name and the right wording, none repeats while the wait goes on, and each is taken down when answered; a second request while one is pending raises nothing and answering the first takes nothing down; an abort and a closing session take the alert down; a shell command and a non-waiting session raise nothing; an idle session alerts under its own name; Mute notifications raises nothing though the row still turns red, and answering still takes down; a notifier that throws neither stops the row turning red nor stops later alerts; a prompt left unanswered from before start-up is red but silent, an answer to it still takes its alert down, and after a restart neither it nor a prompt still waiting rings while a new one does; a session that opens already waiting alerts at once; Preview notifications shows two finished-turn samples and the alert. Against the real `NotificationService` with a fake sound and clock: an alert opens one red balloon marked as an alert with the title and wording, rings once, a second at once and one just inside two seconds stay quiet, one past two seconds rings again, a repeat for a session that has an alert refreshes it in place, a finished-turn balloon for the same session is separate (and silent), dismissing pops only the alert, whichever balloon came first, dismissing twice or a stranger does nothing, a click pops just that one, and 24 finished-turn balloons use 8 colors, none of them the alert's red.
- A one-off probe (`soundprobe`) called the shipped `PlaySound` declaration with the shipped alias and flags (0x210003): Windows accepted it and started the sound.
- Rendered images were looked at: the red balloon next to two finished-turn ones, and the regenerated overview.
- Mutation check on scratch copies, with an unchanged copy as a control that passed all three modes: 26 single-point mutations (the policy's boundary, direction, repeat, dismissal, closing, memory and undated handling; the view model's call, mute, dismissal, wording, failure handling, title, preview sample and start time; the service's color, balloon key, sound throttle, sound call, dismissal target, palette and alert flag; the container registration; showing the alert as a finished-turn balloon). All 26 are killed. When the mutants were planned, the dismissal's target got its own check (the finished-turn balloon created first, since the first balloon found for the session would otherwise be the alert); in the pass that mutant was killed by the neighbouring "dismissing twice does nothing" check as well.
- The whole suite on the finished tree: build 0 warnings, 520 Core tests, and the 12 harness modes (`pinnedonly` 79, `alerts` 57, `attention` 72, `scroll` 47, `noshell` 28, `popup` 14, `pins` 59, `hidden` 30, `hiddenrestart` 21, `nomouse` 51, `di` 5, `fonts` 28). `noshell` failed one timing check once in an earlier run (the start-up retry timer's count under load, in code this change does not touch) and passed three runs straight afterwards.

**Not verified**: hearing the sound (only that Windows accepted it), the alert in the deployed tray (Preview notifications there would ring and leave three balloons on the user's screen, so it was left for them to try), the alert for a real prompt from a live Copilot session (raising one needs a model session in the user's own Copilot state, which was left alone; the fixture logs stand in, in the CLI's real event shapes), and how the balloon and sound behave under Focus Assist (they are a plain window and a system sound, so they are not held back by it).

**Still open**: a switch for the sound alone (today Mute notifications covers the balloons and the sound together); a reminder if a wait goes unanswered for a long time; a short grace period if quickly answered prompts turn out to be noisy; a Windows toast as well as the balloon.

## 10. Milestones

1. **Phase 0 — Spike**: throwaway console app that polls
   `open-sessions-state.json` + tails `events.jsonl` and prints state
   transitions to the console. Validates all assumptions in §2 before any UI
   work; also investigate the per-session `session.db` file found in §2.4.
2. **Phase 0.5 — Contracts review**: write the `Core` interfaces + data
   models from §4.1 (signatures + XML doc comments only, no logic) for
   review/sign-off before any implementation lands. This is the "control and
   understanding" checkpoint — everything after this phase is implementing
   an already-agreed contract. **Done (pending your review)**: solution +
   3 projects scaffolded (`CopilotSessionTray.{Core, Core.Tests, App}`), all
   7 interfaces and their supporting models are written under
   `src\CopilotSessionTray.Core\{Contracts,Models}\`, solution builds clean
   with 0 warnings/errors. No implementations of these interfaces exist yet.
3. **Phase 1 — Tray shell**: icon, context menu, quit, run-at-startup toggle;
   fake/static data only. **Done**: `CopilotSessionTray.App` now shows a real
   system tray icon (`H.NotifyIcon.Wpf`), colored/glyphed by an app-level
   `TrayAggregateState` (`NoSessions`/`Idle`/`Working`/`AttentionNeeded`)
   computed from a static, seeded session list — no file/process reading.
   Right-click context menu: show sessions popup, mark all read, cycle
   between four fixed demo scenarios (to exercise all icon/list states),
   mute toggle (in-memory only), **real** run-at-startup toggle (originally
   a Registry Run key; revised 2026-10-01 to a Scheduled Task — see §9.3 —
   via `Services/StartupRegistration.cs`), **real** "open Copilot
   logs folder" action, and quit. Left-click toggles a small popup window
   (repurposed `MainWindow`) listing the fake sessions with status dot,
   detail snippet, elapsed time, and an unread marker. `CopilotSessionTray.Core`
   is untouched — still contracts/models only, per Phase 0.5.
4. **Phase 2 — Data layer**: implement `Core` behind the reviewed interfaces
   + xUnit tests reading the real sources via fixtures. **Substantially
   complete**: all 9 interfaces (the original 7, plus `ISessionLauncher`/
   `IYoloTaskRunner` added along the way) now have real implementations
   under `src\CopilotSessionTray.Core\Services\`:
   - `OpenSessionsRegistryReader` (`IOpenSessionsRegistryReader`) — reads
     the real `open-sessions-state.json`; format re-verified unchanged
     against a live, currently-populated file while implementing this.
   - `ProcessLivenessChecker` (`IProcessLivenessChecker`) — real
     `Process.GetProcessById`/`GetProcessesByName("copilot")` checks.
   - `SessionHistoryStore` (`ISessionHistoryStore`) — real, read-only
     `Microsoft.Data.Sqlite` access to `session-store.db` (`Mode=ReadOnly`,
     retry-with-backoff on `SQLITE_BUSY`/`SQLITE_LOCKED`); schema
     re-verified against the real, populated DB. All 4 interface methods
     implemented (not just the one needed for the scenario below).
   - `AppStateStore` (`IAppStateStore`) — real, file-based (JSON under
     `%LOCALAPPDATA%\CopilotSessionTray\app-state.json`) persistence for
     read markers, preferences (including the "Start new task" panel's
     remembered workspace directory — see Phase 5 below), and custom
     display names; a `SemaphoreSlim` serializes read-modify-write calls
     within the process. All 6 interface methods implemented.
   - Plus one small **non-interface** helper,
     `SessionLockFileInspector` — reads a session's
     `session-state\<id>\inuse.<pid>.lock` to find its owning pid.
     Deliberately *not* added as an 8th reviewed contract (it doesn't
     cleanly fit any of the 7; this cross-referencing job really belongs to
     `ISessionDetectionEngine` once that's implemented for real) — kept as
     a small, clearly-labeled stand-in rather than unilaterally expanding
     the Phase 0.5 contract surface.
   - Wired into a new 6th "live" tray cycle-demo-data scenario
     (`TrayViewModel.LoadLiveSessionsScenarioAsync`) as a real, working
     proof. **Revised after initial user feedback** — the first version
     only checked "is *any* copilot process running," which wasn't
     enough: empirically, `open-sessions-state.json` entries can persist
     for hours after a session is actually done with, and a single
     long-lived `copilot` process can hold `inuse.<pid>.lock` files in
     *multiple* old session folders at once (e.g. after `/resume`/`/fork`).
     A session now only counts as genuinely open if its lock file's pid is
     currently running (via `SessionLockFileInspector` +
     `IProcessLivenessChecker` — directly implementing the §9 "stuck state
     machine" mitigation) *and* it's the most-recently-refreshed session
     for that pid (older ones sharing the same pid are superseded, not
     open). Each surviving row's title comes from `ISessionHistoryStore`'s
     `summary` column — confirmed to match what VS Code's own session list
     shows for the same session id. **Extended further**: also appends up
     to 10 of the most recently updated genuinely-closed sessions below the
     open ones (same history query as `ShowSessionHistory`, minus anything
     already shown as open — verified no duplicate ids), so this one view
     answers both "what's open" and "what did I just finish" at a glance.
     Deliberately **not yet implemented (as of the section above):**
     `events.jsonl` tailing, `ISessionDetectionEngine`'s real stateful
     diffing/polling loop (this scenario is a one-shot snapshot, not a
     poll), notifications. **Update below (§10 Phase 3): the first two are
     now done.**
   - **2026-09-29: `IAppStateStore` persistence wired up, and 41 xUnit
     tests added** (§9.1) — `Core.Tests` was empty until this pass. All 4
     original services plus the two new ones below got a constructor-
     injectable path override (default unchanged) specifically so tests use
     temp fixtures, never the real `.copilot` folder or `%LOCALAPPDATA%`.
   - **Also shipped in Phase 2, not covered above**: the "Start new task"
     panel (`NewTaskWindow` + `IYoloTaskRunner`/`YoloTaskResult` model — see
     §5 item 12), the session summary/rename panel (`SessionSummaryWindow`),
     per-session Resume/Remove actions (§5 item 13) and the shared
     `ResumeMenuHelper`, and tray double-click opening the "start new task"
     panel directly. Notable fixes made along the way, all verified against
     real data/processes rather than assumed: `wt.exe split-pane` was
     dropped in favor of plain `wt.exe -d <dir> cmd /k <script>` (the former
     always spawned an extra blank pane on a cold start); permissions are
     granted via `COPILOT_ALLOW_ALL=true` in the generated launch script,
     not the `--allow-all` CLI flag (confirmed via `copilot help
     environment` that only the env var also bypasses the folder-trust
     prompt); a `Window.Owner`-before-`Show()` crash (root cause: `MainWindow`
     is deliberately never shown at startup) was fixed with a
     `TryGetOwnerWindow()` guard plus a `DispatcherUnhandledException`
     safety net in `App.xaml.cs`; a tray-icon double-click was found to also
     fire two `TrayLeftMouseUp` events, needing a 250ms debounce timer.
   - **2026-09-29 bug fixes** (see §9.1): the tray context menu's bold
     header was hardcoded to "(demo data)" and never reflected the live
     scenario — now bound to a real `TrayMenuHeaderText` property; and
     `SessionLockFileInspector.GetOwningProcessId` picked an arbitrary lock
     file via `FirstOrDefault()` instead of the most-recently-written one,
     a latent correctness risk for the live-session detection path if a
     session folder ever held more than one lock file.
5. **Phase 3 — Wire-up**: detection engine drives real tray icon state +
   popup session list. **In progress, 2026-09-29**: the two remaining
   unimplemented Core contracts now have real implementations, both fully
   covered by xUnit tests (64 total, up from 41):
   - `SessionEventStreamReader` (`ISessionEventStreamReader`) — tails a
     session's `events.jsonl` from a byte offset; real on-disk shape
     (top-level `type`/`timestamp`/`data`/`id`/`parentId` per line)
     re-verified against a real, populated file. Scans raw bytes (not the
     decoded string) for the newline that ends the last *complete* line, so
     a partial line still being appended mid-write is never parsed and
     `NextByteOffset` never skips past it — also keeps the offset
     byte-exact for multi-byte UTF-8 content. Unrecognized/invalid lines
     degrade gracefully (mapped to `SessionEventKind.Unknown`, or skipped
     entirely if not valid JSON at all) per the interface's contract.
   - `SessionDetectionEngine` (`ISessionDetectionEngine`) — the stateful
     poll-diffing "brain". Reuses the exact same "genuinely open" rule as
     `TrayViewModel.LoadLiveSessionsScenarioAsync` (lock file + process
     liveness + freshest-per-pid), so both paths agree on what counts as
     open. Enforces the §3/§9 "signal authority" rule literally in code:
     `SessionStatus`/`SessionChangeKind` are decided purely from the
     registry's `Working` flag + process liveness on every poll, never
     from an `events.jsonl` event type — `events.jsonl` is tailed only to
     attach `SessionChangeEvent.LatestEvent` as enrichment detail, and a
     failure reading it is swallowed rather than affecting the reported
     status. `Working` flag `true→false` emits `Finished` (status becomes
     `SessionStatus.Finished`, not just `WaitingForInput` — the one
     "candidate for notification" signal, since steady-state idling emits
     nothing further); `false→true` emits `WorkingStateChanged`; a
     session dropping out of the genuinely-open set (process gone, or
     superseded by a fresher session sharing its pid) emits `Closed`.
   - **Deliberately not yet done, pending a decision (see below)**:
     wiring either of these into the running app — i.e. an actual
     recurring poll loop, `INotificationService`'s concrete
     implementation, and replacing/augmenting the manual "cycle to live
     scenario 5" snapshot with continuously-updating real data. Paused
     here rather than guessed at, since the notification mechanism choice
     has real trade-offs (see the open question below) that affect the
     app's distribution model (§11) — the user asked to be paused for
     exactly this kind of decision rather than have it silently assumed.

   **Decision made 2026-09-29 — user was unavailable to confirm, so resolved autonomously per
   the recommendation below (stated as an assumption, per "decide, don't ask" for unresolvable
   ambiguity when no one can weigh in) and then implemented + verified the same day:**
   `INotificationService`'s concrete mechanism —
   1. **Simple** *(chosen)*: `H.NotifyIcon`'s built-in `TaskbarIcon.ShowNotification(title, body, NotificationIcon.Info)`
      (classic `Shell_NotifyIcon` balloon, still renders as a modern toast-style
      popup on Windows 10/11). No extra setup, no new dependency, keeps the
      current xcopy/zip distribution model completely unchanged. No action
      buttons (§6's "Copy resume command · Open folder · Dismiss" would not
      be possible this way) — clicking the balloon reactivates the popup
      instead (`TaskbarIcon.TrayBalloonTipClicked` → same code path as a
      single tray-icon click).
   2. **Rich** *(not chosen — revisit later if wanted)*: `Microsoft.Toolkit.Uwp.Notifications`-style
      Windows App notifications with real action buttons, per §6's original
      sketch. Requires registering an AUMID + a Start Menu shortcut (a small
      one-time setup step, not full MSIX packaging) so Windows knows what
      app owns the toast/its actions — directly touches the "revisit only
      if an AUMID need emerges" note in §11.
   Implemented as `Services.NotificationService` (App project, not Core —
   it's the one real service whose entire job requires a live WPF
   `TaskbarIcon`, so it can't live in UI-agnostic Core the way every other
   real service does). Registered as its own concrete DI singleton (not just
   behind `INotificationService`) so `MainWindow` can call
   `AttachTrayIcon(TrayIcon)` once the tray icon exists, without a circular
   constructor dependency (`MainWindow` → `TrayViewModel` → `NotificationService` → `MainWindow`
   would otherwise result from taking `INotificationService`/`MainWindow` as
   mutual constructor parameters).

   Also decided the same way (no one available to confirm): the app now polls
   **continuously from startup** — `TrayViewModel.StartMonitoring()` (called
   once from `App.OnStartup`, after the tray icon/notification wiring exists)
   starts a 3-second `DispatcherTimer` that calls `ISessionDetectionEngine.PollAsync()`
   for the app's entire lifetime, regardless of which demo scenario (if any)
   is currently displayed or whether the popup is even open — matching this
   app's whole reason to exist (§1: notice a finished session while not
   looking). Design choices worth recording:
   - **Notifications fire unconditionally on every `Finished` transition**
     (mute-gated, checked fresh from `IAppStateStore` each time), but
     **`Sessions` (the visible list) is only patched while `IsShowingLiveData`
     is true** — otherwise a background transition would silently corrupt
     whatever fake demo scenario (0-4) happens to be on screen. Scenario
     5's one-shot `LoadLiveSessionsScenarioAsync` load and the continuous
     monitor's per-tick patching now share one `BuildOpenSessionItemAsync`
     helper (title resolution, rename override, formatting), so both stay
     consistent.
   - A closed session is **patched to `Status = Closed` in place, not
     removed** — so the user can see "that one just closed" rather than a
     row silently vanishing.
   - Re-entrancy guard (`_isPolling`): `PollAsync` is documented as not
     safe to call concurrently; a `DispatcherTimer` doesn't wait for an
     `async void` tick handler, so a slow poll could otherwise overlap
     with the next tick. Ticks are skipped (not queued) while a previous
     one is still running.
   - The already-existing "Mute notifications" checkbox was, until now, a
     pure in-memory toggle with a literal "Phase 2" placeholder comment —
     fixed as part of this same pass (`OnIsMutedChanged` now persists via
     `IAppStateStore`; `StartMonitoring` loads the persisted value at
     startup), since mute needed to be real for notification-gating to
     mean anything.
   - **Not done in this pass, deliberately deferred**: quiet hours/
     watch-list filtering (`NotificationPreferences` already has the
     fields; no settings UI exists yet to set them, and the user has said
     a settings panel is a *future*, not current, ask); a single-instance
     guard (§10 Phase 5 stretch, unrelated to this specific gap); fully
     unifying `LoadLiveSessionsScenarioAsync`'s one-shot "genuinely open"
     resolution with the continuous engine's own internal tracking (they
     independently agree today since both use the same underlying
     sources/rule, but a deeper refactor collapsing them into one could be
     revisited later without correctness pressure right now).

   **Verified empirically** (per this project's established practice — see
   §9.1's earlier "test using the real `App` class" lesson): ran the actual
   compiled exe for 20+ seconds against this machine's real, populated
   `.copilot` folder — stayed responsive, no unhandled-exception dialog (
   confirmed via `EnumWindows`, not just "didn't visibly crash"). Separately,
   a scratch harness constructing the real `TrayViewModel` with all 9 real
   dependencies (temp `IAppStateStore` file, everything else pointed at the
   real `.copilot` folder) confirmed: cycling to the live scenario still
   shows correct real titles post-refactor (13 real sessions, including this
   very session's own live title); toggling `IsMuted` round-trips correctly
   through a *separate, freshly-constructed* `AppStateStore` instance reading
   the same file back; and `StartMonitoring()` completes multiple real poll
   ticks against real data with zero exceptions and no spurious notifications.
6. **Phase 4 — Notifications**: toast on completion, unread badge, mark-as-read.
   **Now substantially complete as of the Phase 3 entry above** — real toast
   (`NotificationService`), real unread badge (`SessionChangeKind.Finished`
   sets `IsUnread = true` on the matching visible row), mark-as-read was
   already wired in an earlier pass. Remaining from this phase's original
   scope: nothing essential; quiet hours/watch-list filtering remain
   unimplemented pending a settings UI (see above).
7. **Phase 5 — Settings & polish**: mute/quiet hours, watch-list filter,
   icon states, auto-start, single-instance guard. Distribution stays
   xcopy/zip (see §11) — auto-start is a per-user Scheduled Task (not an
   installer). **Mute is now real** (see above); **auto-start now includes
   crash-recovery, and a single-instance guard is in place** (2026-10-01,
   see §9.3); quiet hours/watch-list filter remain unstarted.
8. **Phase 6 — Stretch**: history/search view, usage/cost dashboard, toast
   actions.

## 11. Open assumptions to confirm before/while implementing

- **No employer-identifying information anywhere in this repo** (demo data,
  fixtures, comments, commit messages, screenshots) — this is pushed to the
  author's personal GitHub account, not a work one. Use generic placeholder
  org/repo names (`acme/...`, `octocat/...`, `sample-org/...`) for anything
  that looks like a repo or project name. Phase 1's demo data in
  `TrayViewModel.LoadDemoScenario` already follows this; keep it that way
  when Phase 2 adds fixture files for `Core.Tests` too.
- Tech stack defaulted to **.NET 10 WPF (MVVM) + `H.NotifyIcon.Wpf`**
  (originally planned as .NET 8, but only .NET 9/10 SDKs were installed when
  scaffolding started, and .NET 10 is the current LTS — see the skeleton's
  README.md), chosen specifically because a settings panel (and later
  history/usage views) is planned; revisit if a different stack is
  preferred.
- v1 targets **CLI sessions only** (`copilot.exe` + `.copilot\session-state`).
  The `.copilot` folder also appears to be shared with other Copilot
  surfaces (e.g. `vscode.session.metadata.cache.json` suggests VS Code
  Copilot Chat integration) — treated as stretch/out-of-scope for v1 unless
  desired sooner.
- Notification mechanism defaulted to native Windows toast
  (`NotifyIcon`/`ShowBalloonTip` or a small toast library) rather than a
  custom popup window.
- No auto-start-on-login in the MVP; added in Phase 5 unless wanted sooner.
- **Distribution defaults to xcopy/zip** (a plain `dotnet publish` output
  folder), not an installer (MSI/MSIX/etc.) — appropriate for a personal,
  single-machine tool: no admin rights, trivial to remove, no packaging
  tooling to maintain. Revisit only if a real need for a Start Menu entry,
  Control-Panel uninstall, or an AUMID for full Action-Center toast
  actions/history emerges during Phase 4 (see §5/§10) — that would likely be
  solved with a plain Start-Menu shortcut rather than a full installer.
