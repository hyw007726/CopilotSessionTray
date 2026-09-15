namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A row from the <c>sessions</c> table in <c>session-store.db</c>, as
/// returned by <see cref="Contracts.ISessionHistoryStore"/>.
/// </summary>
/// <param name="Id">The session's unique id.</param>
/// <param name="Cwd">The working directory the session was started from, if known.</param>
/// <param name="Repository">The repository the session was working in, if known.</param>
/// <param name="HostType">The surface that hosted the session (e.g. CLI), if known.</param>
/// <param name="Summary">A short human-readable summary of the session, if available.</param>
/// <param name="CreatedAtUtc">When the session was created.</param>
/// <param name="UpdatedAtUtc">When the session was last updated.</param>
public sealed record SessionSummary(
    string Id,
    string? Cwd,
    string? Repository,
    string? HostType,
    string? Summary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
