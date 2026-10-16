namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A user-facing notification payload dispatched via
/// <see cref="Contracts.INotificationService"/> when a watched session
/// finishes.
/// </summary>
/// <param name="SessionId">The session this notification is about.</param>
/// <param name="Title">The session's title (falling back to its repository or folder, then its short id).</param>
/// <param name="Body">A one-line, plain-text snippet of what the session reported when it finished, e.g. its <c>task_complete</c> summary or last assistant message.</param>
/// <param name="ElapsedWorking">How long the session was actively working before finishing, if known.</param>
public sealed record SessionNotification(
    string SessionId,
    string Title,
    string Body,
    TimeSpan? ElapsedWorking);
