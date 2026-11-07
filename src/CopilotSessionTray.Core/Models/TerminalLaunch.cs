namespace CopilotSessionTray.Core.Models;

/// <summary>A terminal this app opened: the palette slot it got and, when it resumed a session, which one.</summary>
/// <param name="Slot">The position of its color in the terminal palette.</param>
/// <param name="SessionId">The session that was resumed, or <see langword="null"/> for a new session.</param>
public sealed record TerminalLaunch(int Slot, string? SessionId);
