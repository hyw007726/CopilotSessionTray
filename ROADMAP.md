# Roadmap — future ideas (not committed, not scheduled)

Unlike [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) (the actual
phased plan this app is being built against), this file is just a holding
pen for "someday / maybe" ideas worth remembering — nothing here is
designed, scoped, or decided yet. Pulled out into its own file specifically
so these don't get lost/forgotten among the implementation plan's much more
detailed, in-progress tracking.

## Desktop pet mode

Instead of (or alongside) a plain tray icon, render Copilot's state as a
small animated on-screen character (idle/working/finished/attention-needed
poses) that lives on the desktop rather than hiding in the tray. The
existing `TrayAggregateState` (`NoSessions`/`Idle`/`Working`/
`AttentionNeeded`) already models exactly the states such a character would
react to, so the detection/state layer (`Core`) likely needs no changes at
all — this would be a new, purely presentational `App`-layer view (probably
a small transparent always-on-top window) alongside, not replacing, the
tray icon.

## macOS version

A native port so this isn't Windows-only. `CopilotSessionTray.Core` was
kept free of WPF/Windows-UI dependencies specifically to make this kind of
reuse possible, but several real services still assume Windows and would
need macOS-specific replacements:

- `IProcessLivenessChecker` (`Process.GetProcessesByName` assumptions)
- `SessionLockFileInspector` (Windows-style `inuse.<pid>.lock` handling)
- `CopilotTerminalLauncher` (`wt.exe`/`cmd.exe`-specific launch scripting)
- The tray UI itself (`H.NotifyIcon.Wpf` → something like a `NSStatusItem`
  under AppKit/SwiftUI, or a cross-platform UI framework)

`session-store.db`/`.copilot` folder *reading* logic should port with
little change, since that's just file/SQLite access, not Windows-specific
— the real work is a new `CopilotSessionTray.App.Mac`-equivalent project
reusing `Core` as-is.
