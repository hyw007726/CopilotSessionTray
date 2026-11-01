namespace CopilotSessionTray.Core.Models;

/// <summary>Whether a session is blocked on the user, and since when.</summary>
/// <param name="Kind">What it is waiting for; <see cref="PendingPromptKind.None"/> when it is not blocked.</param>
/// <param name="SinceUtc">When the oldest unanswered request was logged, if its event carried a timestamp.</param>
public readonly record struct PendingPrompt(PendingPromptKind Kind, DateTimeOffset? SinceUtc = null)
{
    public bool IsPending => Kind != PendingPromptKind.None;
}
