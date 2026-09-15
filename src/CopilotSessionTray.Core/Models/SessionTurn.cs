namespace CopilotSessionTray.Core.Models;

/// <summary>
/// A row from the <c>turns</c> table in <c>session-store.db</c>.
/// </summary>
/// <param name="SessionId">The owning session's id.</param>
/// <param name="TurnIndex">The turn's position within the session, starting at 0.</param>
/// <param name="UserMessage">The user's message for this turn, if any.</param>
/// <param name="AssistantResponse">The assistant's response for this turn, if any.</param>
/// <param name="TimestampUtc">When this turn was recorded.</param>
public sealed record SessionTurn(
    string SessionId,
    int TurnIndex,
    string? UserMessage,
    string? AssistantResponse,
    DateTimeOffset TimestampUtc);
