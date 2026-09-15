# Architecture (high level, Phase 0.5)

> Open this file with **Markdown Preview** in VS Code (`Ctrl+Shift+V`, or right-click
> the tab → "Open Preview") to render the diagram below. VS Code renders Mermaid
> natively in Markdown Preview (no extension needed on recent versions). If it
> doesn't render, paste the code block into <https://mermaid.live> instead.

## 1. Solution structure

Which project depends on which — this part is fully built and compiles today.

```mermaid
flowchart LR
    Core["CopilotSessionTray.Core<br/>Contracts + Models"]
    App["CopilotSessionTray.App<br/>WPF shell (default template)"]
    Tests["CopilotSessionTray.Core.Tests<br/>xUnit (empty)"]

    App -->|ProjectReference| Core
    Tests -->|ProjectReference| Core

    classDef proj fill:#E8F0FE,stroke:#3A6EA5,stroke-width:1.5px,color:#1a1a1a
    class Core,App,Tests proj
```

## 2. Data flow through Core (the actual design)

Read left → right as a pipeline: raw Copilot CLI files/processes are read by
dedicated contracts, funneled into the detection engine (the "brain"), which
drives notifications and, eventually, the tray UI. **Yellow/dashed = interface
only, no implementation yet.** Orange = the most important piece to review
closely. Green = the end-user-visible payoff this whole pipeline exists for.

```mermaid
flowchart LR
    subgraph SRC["Copilot CLI local state"]
        direction TB
        S1["open-sessions-state.json"]
        S2["events.jsonl (per session)"]
        S3["inuse.&lt;pid&gt;.lock (per session)"]
        S4["session-store.db"]
        S5["OS processes"]
    end

    subgraph CORE["CopilotSessionTray.Core — contracts only, no implementation yet"]
        direction TB
        C1["IOpenSessionsRegistryReader"]
        C2["ISessionEventStreamReader"]
        C3["ISessionHistoryStore"]
        C4["IProcessLivenessChecker"]
        C5["IAppStateStore<br/>(our own read-markers + prefs)"]
        C6["ISessionDetectionEngine<br/>(diffs polls → change events)"]
        C7["INotificationService"]
    end

    OUT["Tray icon state + toast notification<br/>(future App UI)"]

    S1 --> C1
    S2 --> C2
    S4 --> C3
    S3 --> C4
    S5 --> C4

    C1 --> C6
    C2 --> C6
    C4 --> C6
    C5 -.provides prefs/markers.-> C6
    C6 --> C7
    C7 --> OUT

    classDef src fill:#F2F2F2,stroke:#888888,color:#222222
    classDef iface fill:#FDF3D8,stroke:#C9A227,stroke-width:1px,stroke-dasharray: 4 4,color:#222222
    classDef brainNode fill:#FCE8D6,stroke:#D9822B,stroke-width:2.5px,color:#222222
    classDef out fill:#E4F7E4,stroke:#3A9B4C,stroke-width:1.5px,color:#222222

    class S1,S2,S3,S4,S5 src
    class C1,C2,C3,C4,C5,C7 iface
    class C6 brainNode
    class OUT out
```

**Models** (`OpenSessionEntry`, `SessionEventRecord`, `SessionSummary`,
`SessionTurn`, `SessionCheckpoint`, `SessionReadMarker`,
`NotificationPreferences`, `SessionChangeEvent`, `SessionNotification`,
plus the `SessionStatus`/`SessionEventKind`/`SessionChangeKind` enums) are
omitted above for clarity — they're just the plain data shapes each
contract above passes around; see `src/CopilotSessionTray.Core/Models/`.

Matches the Phase 0.5 status described in
[`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md).
