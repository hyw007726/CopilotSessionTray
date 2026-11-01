namespace CopilotSessionTray.Core.Models;

/// <summary>What a session is waiting on the user for, as far as its event log shows.</summary>
public enum PendingPromptKind
{
    /// <summary>Nothing: the session is not blocked on the user.</summary>
    None = 0,

    /// <summary>A permission request for a command, file, URL or tool awaits an approval or a denial.</summary>
    Permission,

    /// <summary>The agent asked a question, or asked for a plan to be approved, and awaits the answer.</summary>
    Question,
}
