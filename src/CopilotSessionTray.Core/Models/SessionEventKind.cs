namespace CopilotSessionTray.Core.Models;

/// <summary>
/// Known <c>events.jsonl</c> event types observed in Copilot CLI session
/// folders. <see cref="Unknown"/> is used for any event type not yet
/// recognized, so new/unmapped event types degrade gracefully instead of
/// throwing (see risk notes in IMPLEMENTATION_PLAN.md, &#167;9).
/// </summary>
public enum SessionEventKind
{
    /// <summary>Event type was not recognized; see the record's raw type/JSON instead.</summary>
    Unknown = 0,

    /// <summary>Corresponds to the observed <c>assistant.turn_start</c> event type.</summary>
    AssistantTurnStart,

    /// <summary>Corresponds to the observed <c>assistant.message</c> event type.</summary>
    AssistantMessage,

    /// <summary>Corresponds to the observed <c>tool.execution_start</c> event type.</summary>
    ToolExecutionStart,

    /// <summary>Corresponds to the observed <c>tool.execution_complete</c> event type.</summary>
    ToolExecutionComplete,

    /// <summary>Corresponds to the observed <c>assistant.turn_end</c> event type — a primary "finished" signal.</summary>
    AssistantTurnEnd,

    /// <summary>Corresponds to the observed <c>session.task_complete</c> event type — a primary "finished" signal.</summary>
    SessionTaskComplete,

    /// <summary>Corresponds to the observed <c>session.usage_checkpoint</c> event type.</summary>
    SessionUsageCheckpoint,

    /// <summary>Corresponds to the observed <c>session.shutdown</c> event type — the process exited cleanly.</summary>
    SessionShutdown,
}
