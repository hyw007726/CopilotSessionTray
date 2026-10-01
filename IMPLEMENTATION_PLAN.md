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
| `INotificationService` | Dispatch a toast/notification for a finished session; fakeable in tests, swappable if the notification mechanism changes later. **Implemented and wired** (`Services.NotificationService`, classic `TaskbarIcon.ShowNotification` balloon — see §9.1/§10 Phase 3 for the mechanism decision, made autonomously since the user was unavailable to confirm). |
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
  `AppStateStore`'s in-process `SemaphoreSlim` doesn't protect
  `app-state.json` against two *process* instances writing at once,
  relevant only once §5 item 9's single-instance guard is being designed.

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
   mute toggle (in-memory only), **real** run-at-startup toggle (Registry
   Run key, via `Services/StartupRegistration.cs`), **real** "open Copilot
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
   xcopy/zip (see §11) — auto-start is just a Startup-folder shortcut, no
   installer involved. **Mute is now real** (see above); the rest is
   unstarted.
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
