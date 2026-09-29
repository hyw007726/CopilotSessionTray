# Copilot Session Tray

Personal Windows tray utility to monitor GitHub Copilot CLI sessions: see at
a glance whether Copilot is running, whether any session is actively
working, and get notified when a session finishes unattended.

See [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) for the full design,
research findings, architecture, and roadmap.

## Status

**Phase 1 done; Phase 2 substantially complete.** A real Windows tray icon
shows up (via `H.NotifyIcon.Wpf`), with a context menu, a click-to-show
popup, and a working run-at-startup toggle. `CopilotSessionTray.Core` now
has real, working implementations of all 9 contracts behind
`Services/` — reading the real `open-sessions-state.json`/`session-store.db`/
`inuse.<pid>.lock` files, checking real process liveness, launching real
terminal sessions (resume/start-from-summary/start-new-task/headless), and
persisting this app's own read markers, preferences, and display-name
overrides under `%LOCALAPPDATA%\CopilotSessionTray\`. "Cycle demo data" in
the tray menu still cycles through 5 fixed fake scenarios (to exercise
tray icon/list states on demand) plus a 6th **live** scenario showing your
real currently-open and recently-closed sessions. Not yet implemented:
`ISessionDetectionEngine`'s real polling/diffing loop, `ISessionEventStreamReader`,
and `INotificationService` — today's live view is a manual one-shot
snapshot, not an automatic background poll with toast notifications; that's
Phase 3/4. See [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) §9.1/§10
for the full up-to-date picture, including a 2026-09-29 design-review pass
and how each finding was resolved.

## Project layout

```
CopilotSessionTray.slnx
src/
  CopilotSessionTray.Core/            Real service implementations + Contracts + Models (no UI deps)
    Contracts/                        The 9 interfaces from IMPLEMENTATION_PLAN.md §4.1 (7 original + 2 added later)
    Models/                           Plain records/enums used by the contracts
    Services/                         Real implementations: registry/history/lock-file readers, process
                                       liveness, app-state persistence, terminal/headless task launching
  CopilotSessionTray.Core.Tests/      xUnit tests (41), fixture-based — every service takes an optional
                                       path override so tests never touch the real .copilot folder
  CopilotSessionTray.App/             WPF tray shell, DI composition root in App.xaml.cs
    ViewModels/                       TrayViewModel (demo scenarios + live data) + SessionItemViewModel
    Services/                         StartupRegistration (real Registry Run-key toggle; not Copilot-related)
    MainWindow.xaml                   Hosts the TaskbarIcon + doubles as the click-to-show session popup
```

## Try it

```powershell
dotnet run --project src\CopilotSessionTray.App\CopilotSessionTray.App.csproj
```

A tray icon appears in the notification area (colored dot / unread count).
Left-click toggles the session popup; double-click opens "Start new task…"
directly. Right-click opens the context menu (show sessions, start new
task…, session history…, mark all read, cycle demo data, mute toggle,
run-at-startup toggle, open Copilot logs folder, quit). Quit is the only
way to fully exit — closing the popup window just hides it.

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
