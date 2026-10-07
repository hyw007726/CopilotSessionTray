# Copilot Session Tray

Personal Windows tray utility to monitor GitHub Copilot CLI sessions: see at
a glance whether Copilot is running, whether any session is actively
working, and get notified when a session finishes unattended.

See [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) for the full design,
research findings, architecture, and phased plan. See
[`ROADMAP.md`](./ROADMAP.md) for future ideas (desktop pet mode, a macOS
port) that aren't scheduled yet.

## Status

**Phases 1-4 complete.** A real Windows tray icon shows up (via
`H.NotifyIcon.Wpf`): a constant gold Guanyin glyph, with a spinning
gold-to-white gradient ring around it while any session is actively
working (2026-10-06 redesign, replacing an earlier flat green/orange/gray
circle + unread-count/working-dot text glyph; see IMPLEMENTATION_PLAN.md
§9.7 and its same-day follow-ups; the ring itself was briefly flat green,
changed the same day to a gold-to-white gradient so it reads as part of the
glyph's own gold theme). The tray icon is
hand-composited with `System.Drawing.Graphics` (a dynamic rotating arc
isn't expressible through H.NotifyIcon's built-in
"colored shape + centered text" `GeneratedIconSource` renderer at all),
not generated via any reactive `TaskbarIcon.IconSource` binding — that
binding path was the real root cause of a recurring GDI+ crash (an
un-awaited, un-cancelled async call inside H.NotifyIcon, racing on GDI+
objects whenever two icon updates land close together — reachable from
nothing more than ordinary rapid clicks on "Cycle demo data"); the icon is
instead set directly (`TaskbarIcon.Icon`) from code-behind, synchronously,
on the UI thread, making overlapping updates structurally impossible
rather than just less likely (see IMPLEMENTATION_PLAN.md §9.6's final
follow-up). Building the spinning-ring redesign also surfaced a second,
independent, previously-unnoticed bug: a native GDI icon handle leak on
every single icon update (confirmed from .NET's and H.NotifyIcon's own
source — `Icon.FromHandle()` never actually owns the handle it wraps, so
disposing it is a no-op at the native level) — harmless at the old, rare
update frequency, but would have exhausted this process's GDI handle
budget within minutes once a timer started updating the icon ~8 times/sec.
Fixed by manually destroying each previous handle; verified via an
extended soak test (this process's real GDI object count, sampled every
5s across 60s/~500 icon regenerations, stayed perfectly flat) — see
IMPLEMENTATION_PLAN.md §9.7. Context menu, click-to-show popup, and a
working run-at-startup toggle round out the shell. Sessions sitting
open-but-not-working render as a neutral "Idle" rather than a confident
"waiting for input" or "completed" claim — Copilot CLI's own data can't
actually distinguish "paused mid-conversation" from "finished a while
ago", so this app doesn't pretend to (the enum values/UI slots for a real
distinction are kept as an intentional, documented stub — not deleted —
ready to wire up if a future Copilot CLI version exposes a genuine
signal; see IMPLEMENTATION_PLAN.md §9.6's later follow-ups). A finished
session renders identically to an idle one, visually — there's no
separate "unread" tracking any more (a persistent, dismissable
notification balloon covers that job instead — see "Try it" below; the
red tray-dot/orange-row-color/"Mark read" machinery this used to need was
removed once the balloon made it redundant, see IMPLEMENTATION_PLAN.md
§9.7's final follow-up). The popup shows the same
Guanyin artwork as a faint, state-colored watermark behind the session
list (green/gold/gray, matching working/idle-like/empty —
see IMPLEMENTATION_PLAN.md §9.7's follow-ups); each session row's
status text is colored the same as its dot (or, for "Idle"/Finished
rows, the same small gold glyph as the tray icon in place of a plain dot
— see IMPLEMENTATION_PLAN.md §9.6's later follow-ups). `CopilotSessionTray.Core` has real, working
implementations of all 9 contracts behind `Services/` — reading the real
`open-sessions-state.json`/`session-store.db`/`inuse.<pid>.lock` files,
checking real process liveness, launching real terminal sessions
(resume/start-from-summary/start-new-task/headless, each in its own
distinctly-colored terminal pane), and persisting this app's own
dismissal markers, preferences, and display-name overrides under
`%LOCALAPPDATA%\CopilotSessionTray\`. The app starts directly on the
**live** view (your real currently-open + 10 most-recent closed sessions);
"Cycle demo data" still walks 5 fixed fake scenarios on demand. A
continuous background monitor (`ISessionDetectionEngine`, polling every
3s) now drives real toast notifications (`INotificationService`) when a
session finishes, with no single event type trusted as sole authority for
working/idle state (self-correcting against the exact "stuck" bug found in
GitHub's own official taskbar-presence feature — see
IMPLEMENTATION_PLAN.md §3/§9). Wiring is via
`Microsoft.Extensions.DependencyInjection`, composed once in
`App.xaml.cs`. Run-at-startup (optional, toggled from the tray menu) uses
a Scheduled Task with crash-recovery, not a plain Registry Run key — see
IMPLEMENTATION_PLAN.md §9.3. See [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md)
§9.1-§9.7/§10 for the full up-to-date picture: the 2026-09-29 design-review
pass, four same-day bug fixes/features from 2026-10-01 ("Resume with
Summary" prompt quality + provenance header, live view as the startup
default, per-session terminal colors), the same-day startup-reliability
revision (§9.3), the 2026-10-02 popup watermark artwork (§9.4) plus the
real crash/fix/revert that followed it (§9.5), the 2026-10-05 palette
unification/priority rework/bounded flash/dead-code cleanup (§9.6) and its
several same-day follow-ups, and the 2026-10-06 tray icon redesign to a
composited glyph+spinning-ring (§9.7), including its follow-up removing
the separate "unread" tracking concept once the persistent notification
balloon made it redundant.

## Project layout

```
CopilotSessionTray.slnx
src/
  CopilotSessionTray.Core/            Real service implementations + Contracts + Models (no UI deps)
    Contracts/                        The 9 interfaces from IMPLEMENTATION_PLAN.md §4.1 (7 original + 2 added later)
    Models/                           Plain records/enums used by the contracts
    Services/                         Real implementations: registry/history/lock-file readers, process
                                       liveness, app-state persistence, terminal/headless task launching,
                                       the SessionDetectionEngine poll/diff "brain", events.jsonl tailing
  CopilotSessionTray.Core.Tests/      xUnit tests (69), fixture-based — every service takes an optional
                                       path override so tests never touch the real .copilot folder
  CopilotSessionTray.App/             WPF tray shell, DI composition root in App.xaml.cs
    ViewModels/                       TrayViewModel (demo scenarios + live data + background monitor) + SessionItemViewModel
    Services/                         StartupRegistration (Registry Run-key toggle) + NotificationService
                                       (real toast balloons via H.NotifyIcon — Core stays UI-agnostic)
    MainWindow.xaml                   Hosts the TaskbarIcon + doubles as the click-to-show session popup
```

## Try it

```powershell
dotnet run --project src\CopilotSessionTray.App\CopilotSessionTray.App.csproj
```

A tray icon appears in the notification area (gold Guanyin glyph; a
spinning gold-to-white ring while a session is working — see
IMPLEMENTATION_PLAN.md §9.7). Left-click toggles the session popup.
Double-click opens "Start new task…" directly. Right-click opens the context menu (show
sessions, start new task…, session history…, cycle demo
data, mute toggle, run-at-startup toggle, open Copilot logs folder, quit).
A background monitor polls every 3s and shows a persistent notification
(a custom popup that stays open until clicked, not a native OS balloon —
those are capped at 10-30s by Windows itself, not overridable; see
IMPLEMENTATION_PLAN.md §9.7's follow-up) when a session finishes (unless
muted); clicking it just dismisses it — it doesn't open the popup too,
since there's no way for this app to actually jump to the session's own
already-open terminal anyway, so forcing another window open wouldn't add
any real navigation value (see IMPLEMENTATION_PLAN.md §9.7's later
follow-up). Dismissing this notification is the only acknowledgment step
there is — there's no separate "unread" state to clear anywhere else (see
IMPLEMENTATION_PLAN.md §9.7's final follow-up).
Each terminal this app opens (resume/start new task) gets its own
distinct background color so concurrent panes are easy to tell apart.
Quit is the only way to fully
exit — closing the popup window just hides it.

## Requirements

- .NET 10 SDK (targets `net10.0` / `net10.0-windows`). The plan originally
  called for .NET 8, but only .NET 9 and .NET 10 SDKs were available on this
  machine at the time this was scaffolded, and .NET 10 is the current LTS —
  see IMPLEMENTATION_PLAN.md §11.

## Build & test

```powershell
dotnet build CopilotSessionTray.slnx
dotnet test src\CopilotSessionTray.Core.Tests\CopilotSessionTray.Core.Tests.csproj
```
