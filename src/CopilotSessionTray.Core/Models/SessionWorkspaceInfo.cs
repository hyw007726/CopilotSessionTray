namespace CopilotSessionTray.Core.Models;

/// <summary>
/// The parts of a session's <c>workspace.yaml</c> (in <c>%USERPROFILE%\.copilot\session-state\&lt;id&gt;\</c>)
/// this app uses, as read by <see cref="Services.SessionWorkspaceReader"/>.
/// </summary>
/// <param name="Name">The session's current name, or null if it has none.</param>
/// <param name="UserNamed">True when the user chose <paramref name="Name"/> themselves (Copilot CLI's <c>/rename</c>) rather than it being generated.</param>
/// <param name="Cwd">The folder the session was started from, or null if the file does not record one.</param>
/// <param name="Repository">The repository the session works in, or null if the file does not record one.</param>
public sealed record SessionWorkspaceInfo(string? Name, bool UserNamed, string? Cwd = null, string? Repository = null);
