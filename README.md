# Copilot Session Tray

Personal Windows tray utility to monitor GitHub Copilot CLI sessions: see at
a glance whether Copilot is running, whether any session is actively
working, and get notified when a session finishes unattended.

See [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) for the full design,
research findings, architecture, and roadmap.

## Status

**Phase 1 — Tray shell (fake/static data).** A real Windows tray icon now
shows up (via `H.NotifyIcon.Wpf`), with a context menu, a click-to-show
popup listing sessions, and a working run-at-startup toggle. **All session
data is static/fake** — seeded in `TrayViewModel`, cycled via "Cycle demo
data" in the tray menu — nothing yet reads real Copilot CLI files or
processes. `CopilotSessionTray.Core` is still contracts + models only (from
Phase 0.5), unimplemented and untouched by this phase.

## Project layout

```
CopilotSessionTray.slnx
src/
  CopilotSessionTray.Core/            Contracts + Models (no UI deps, no implementations yet)
    Contracts/                        The 7 interfaces reviewed in IMPLEMENTATION_PLAN.md §4.1
    Models/                           Plain records/enums used by the contracts
  CopilotSessionTray.Core.Tests/      xUnit test project (empty until Phase 2)
  CopilotSessionTray.App/             WPF tray shell (Phase 1: fake/static data only)
    ViewModels/                       TrayViewModel (fake session list, demo scenarios) + SessionItemViewModel
    Services/                         StartupRegistration (real Registry Run-key toggle; not Copilot-related)
    MainWindow.xaml                   Hosts the TaskbarIcon + doubles as the click-to-show session popup
```

## Try it

```powershell
dotnet run --project src\CopilotSessionTray.App\CopilotSessionTray.App.csproj
```

A tray icon appears in the notification area (colored dot / unread count).
Left-click toggles the session popup; right-click opens the context menu
(cycle demo data, mark all read, mute toggle, run-at-startup toggle, open
Copilot logs folder, quit). Quit is the only way to fully exit — closing the
popup window just hides it.

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
