namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A user-facing notification payload dispatched via
/// <see cref="Contracts.INotificationService"/> when a watched session
/// finishes.
/// </summary>
/// <param name="SessionId">The session this notification is about.</param>
/// <param name="Title">Short title, e.g. repository/folder name + short session id.</param>
/// <param name="Body">Truncated summary, e.g. latest assistant message or checkpoint title.</param>
/// <param name="ElapsedWorking">How long the session was actively working before finishing, if known.</param>
public sealed record SessionNotification(
    string SessionId,
    string Title,
    string Body,
    TimeSpan? ElapsedWorking);
