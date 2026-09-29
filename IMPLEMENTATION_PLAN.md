# Copilot Session Tray — Implementation Plan

Status: **Phase 1 done; Phase 2 substantially complete — all 9 Core interfaces now have real implementations (see §10), 41 xUnit tests passing, real persistence wired end-to-end. Phase 0.5 contracts still pending your review. See §9.1 for the 2026-09-29 design-review pass and how each finding was resolved.**
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
| `ISessionEventStreamReader` | Tail a session's `events.jsonl` from a given offset; yield new events (`turn_end`, `task_complete`, `shutdown`, ...). |
| `ISessionHistoryStore` | Read-only queries against `session-store.db` (`sessions`, `turns`, `checkpoints`) for metadata/history/search. |
| `IProcessLivenessChecker` | Given a PID (from an `inuse.<pid>.lock` file), report whether the owning process is alive; list running `copilot` processes. |
| `IAppStateStore` | Persist/retrieve our own state: last-seen markers per session, mute/watch-list/quiet-hours preferences — separate from `.copilot`. |
| `ISessionDetectionEngine` | The "brain": consumes the readers above, diffs successive polls, and emits state-change events (`SessionStarted`, `SessionWorkingChanged`, `SessionFinished`, `SessionClosed`). Most important contract to review closely since it defines the whole detection model. |
| `INotificationService` | Dispatch a toast/notification for a finished session; fakeable in tests, swappable if the notification mechanism changes later. |
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
     Deliberately **not yet implemented**:
     `events.jsonl` tailing, `ISessionDetectionEngine`'s real stateful
     diffing/polling loop (this scenario is a one-shot snapshot, not a
     poll), notifications.
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
   popup session list.
6. **Phase 4 — Notifications**: toast on completion, unread badge, mark-as-read.
7. **Phase 5 — Settings & polish**: mute/quiet hours, watch-list filter,
   icon states, auto-start, single-instance guard. Distribution stays
   xcopy/zip (see §11) — auto-start is just a Startup-folder shortcut, no
   installer involved.
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
