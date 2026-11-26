namespace CopilotSessionTray.Core.Models;

/// <summary>What was typed into the "Start a new task" panel and not started yet.</summary>
/// <param name="Prompt">The prompt, exactly as typed.</param>
/// <param name="WorkspaceDirectory">The workspace folder chosen, or empty when none was.</param>
/// <param name="EnableAllPermissions">Whether "Enable all permissions" was ticked.</param>
public sealed record NewTaskDraft(string Prompt, string WorkspaceDirectory, bool EnableAllPermissions);
