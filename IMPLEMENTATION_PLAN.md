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
