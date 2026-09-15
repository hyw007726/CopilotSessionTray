namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A single entry from the live <c>%USERPROFILE%\.copilot\open-sessions-state.json</c>
/// registry, as read by <see cref="Contracts.IOpenSessionsRegistryReader"/>.
/// </summary>
/// <param name="SessionId">The session's unique id (matches the folder name under <c>session-state\</c>).</param>
/// <param name="SchemaVersion">Schema version reported by Copilot CLI for this entry.</param>
/// <param name="OpenedAtUtc">When the session was first opened.</param>
/// <param name="Working">Whether the agent is currently, actively processing a turn.</param>
/// <param name="RefreshedAtUtc">Last time this entry was heartbeat-refreshed by the owning process.</param>
public sealed record OpenSessionEntry(
    string SessionId,
    int SchemaVersion,
    DateTimeOffset OpenedAtUtc,
    bool Working,
    DateTimeOffset RefreshedAtUtc);
