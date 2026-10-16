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
`H.NotifyIcon.Wpf`): a full-bleed gold Guanyin glyph whose folded palms give
off a soft golden-white light that breathes outward through a radial gradient
(bright centre fading to the plain gold at the edges) while any session is
actively working, and stays plain gold when nothing is (2026-10-08 redesign,
replacing the 2026-10-06 spinning gold-to-white ring — which itself replaced
an earlier flat green/orange/gray circle + unread-count/working-dot text
glyph; see IMPLEMENTATION_PLAN.md §9.7 and its follow-ups). The glyph is
drawn by `TrayIconRenderer` at exactly the
pixel size Windows shows tray icons at, slightly emboldened, rather than
handing the shell a large icon to point-sample down (which dropped the
thin halo/hands and made the figure look small and sparse next to other
tray icons); the native icon is built with `CreateIconIndirect` from
straight-alpha pixels rather than `Bitmap.GetHicon()`, because GDI+ stores
premultiplied colour there that the shell then multiplies by alpha a second
time, darkening every anti-aliased edge pixel. The tray icon is hand-rendered with `System.Drawing`
(not expressible through H.NotifyIcon's built-in
"colored shape + centered text" `GeneratedIconSource` renderer at all),
not generated via any reactive `TaskbarIcon.IconSource` binding — that
binding path was the real root cause of a recurring GDI+ crash (an
un-awaited, un-cancelled async call inside H.NotifyIcon, racing on GDI+
objects whenever two icon updates land close together — reachable from
nothing more than ordinary rapid clicks on "Cycle demo data"); the icon is
instead set directly (`TaskbarIcon.Icon`) from code-behind, synchronously,
on the UI thread, making overlapping updates structurally impossible
rather than just less likely (see IMPLEMENTATION_PLAN.md §9.6's final
follow-up). Building the animated tray icon also surfaced a second,
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
several same-day follow-ups, the 2026-10-06 tray icon redesign to a
composited glyph+spinning-ring (§9.7), its follow-up removing
the separate "unread" tracking concept once the persistent notification
balloon made it redundant, the 2026-10-08 follow-up that replaced the
ring with an exact-size, bolder glyph, and its same-day follow-ups: a
radial glow from the folded palms in place of the whole-icon tint, and
stacked, individually dismissible notification cards — which the 2026-10-09
follow-up then turned into colorful balloons that float up the right edge of
the screen, and a later same-day review of the summary logic that fixed
corrupted non-ASCII text in launch scripts, the resume-seed composition, frozen
elapsed labels and a startup duplicate-row race, then the choices that review
left open: session titles taken from your own `/rename` and the checkpoints,
richer resume seeds, balloons that say what the turn did, and a resumed-from
note on the rows, and finally a Summary panel that shows a session's summary
the way you'd read it instead of the text sent to Copilot.

## Project layout

```
CopilotSessionTray.slnx
src/
  CopilotSessionTray.Core/            Real service implementations + Contracts + Models (no UI deps)
    Contracts/                        The 9 interfaces from IMPLEMENTATION_PLAN.md §4.1 (7 original + 2 added later)
    Models/                           Plain records/enums used by the contracts
    Services/                         Real implementations: registry/history/lock-file readers, process
                                       liveness, app-state persistence, terminal/headless task launching,
                                       session title + resume-seed resolution (workspace.yaml name,
                                       checkpoints, the database), the reader-facing summary view and the
                                       small markdown reader behind it, the SessionDetectionEngine
                                       poll/diff "brain" (incl. what each turn reported and how long it
                                       took), events.jsonl tailing
  CopilotSessionTray.Core.Tests/      xUnit tests (303), fixture-based — every service takes an optional
                                       path override so tests never touch the real .copilot folder (the
                                       launch-script tests also run the generated script through cmd.exe)
  CopilotSessionTray.App/             WPF tray shell, DI composition root in App.xaml.cs
    ViewModels/                       TrayViewModel (demo scenarios + live data + background monitor) + SessionItemViewModel
    Services/                         StartupRegistration (Registry Run-key toggle) + NotificationService
                                       (one floating balloon per finished session — Core stays UI-agnostic)
    MainWindow.xaml                   Hosts the TaskbarIcon + doubles as the click-to-show session popup
    BalloonWindow.xaml                One notification balloon: a transparent, non-activating window drawn as a glossy party balloon
    SessionSummaryWindow.xaml         The "🗒 Summary" panel: a session's summary as formatted, selectable text (SummaryDocumentBuilder.cs)
    TrayIconRenderer.cs               Draws the tray icon: exact-size, emboldened gold glyph + a radial glow from the palms
```

## Try it

```powershell
dotnet run --project src\CopilotSessionTray.App\CopilotSessionTray.App.csproj
```

A tray icon appears in the notification area (a full-bleed gold Guanyin
glyph whose folded palms glow, fading outward, while a session is working — see
IMPLEMENTATION_PLAN.md §9.7's final follow-ups). Left-click toggles the session popup.
Double-click opens "Start new task…" directly. Right-click opens the context menu (show
sessions, start new task…, session history…, cycle demo
data, preview notifications, mute toggle, run-at-startup toggle, open Copilot logs folder, quit).
A background monitor polls every 3s and, when a session finishes (unless
muted), floats a colorful balloon up the right edge of the screen from just
above the taskbar. The balloons gather at the top right, stacked top-down in
the order the sessions finished, each titled with the session's name and
showing the first lines of what the turn reported (its `task_complete` summary,
or the last thing the agent said) with "Worked 12m" in the hint line; a turn
that took under 30 seconds doesn't get one. They stay until you pop them by
clicking (they're plain windows this
app draws, not native OS balloons — those are capped at 10-30s by Windows
itself, not overridable; see IMPLEMENTATION_PLAN.md §9.7's follow-ups). Each
session gets its own balloon in its own color; a repeat completion from a
session that already has one refreshes it with a little wobble rather than
adding a duplicate. Clicking a balloon pops just that one and the rest float
up to fill the gap; when more are up than fit at full size they overlap
(newest in front) so every one stays on screen and clickable. Balloons never
take keyboard focus from whatever you're typing in, and clicks on the
transparent space around a balloon go to the window behind it. "Preview
notifications" in the tray menu shows three sample balloons, so you can see
the look without waiting for a session to finish.
Popping a balloon just dismisses it — it doesn't open the popup too,
since there's no way for this app to actually jump to the session's own
already-open terminal anyway, so forcing another window open wouldn't add
any real navigation value (see IMPLEMENTATION_PLAN.md §9.7's later
follow-up). Dismissing this notification is the only acknowledgment step
there is — there's no separate "unread" state to clear anywhere else (see
IMPLEMENTATION_PLAN.md §9.7's final follow-up).
A session's name — in the popup, the balloons and the Summary panel — is your
own `/rename` if you gave it one, otherwise the latest checkpoint's title,
otherwise the session database's summary (usually the first prompt, cut
short); renaming it in this app overrides all of those. A session that was
started with "Resume with summary" shows `↪ resumed from "<original title>"`
on its detail line, and the seed it passes on carries the checkpoint's next
steps and asks the new session to acknowledge in one line and wait.
"🗒 Summary" shows a session's summary the way you'd read it: where it came
from ("Checkpoint 2 · 38m ago", or "No checkpoint yet · recap of the first
request and the latest reply"), then labelled sections (Overview and Next
steps, or First request and Latest reply) with bold, italics, inline code,
bulleted and numbered lists, code blocks and headings drawn instead of shown as
raw markdown. The window is resizable and the text selectable. The exact text a
new session would be seeded with, including the header naming this session's
id, sits behind "Text sent to Copilot when starting a new session from this
summary", with a Copy button. The summary is re-read each time the panel opens
and before "Resume with summary", so a checkpoint written while the app was
running shows up without a restart.
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
