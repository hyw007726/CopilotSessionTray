namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Launches an external terminal to interact with a Copilot CLI session —
/// either resuming one exactly as-is, or starting a brand new session
/// seeded with just a summary of a prior one. The one contract in Core
/// whose entire purpose is a real, user-visible side effect (spawning a
/// process) rather than reading state. It gets its own interface for
/// exactly that reason: callers can fake or record "a launch was requested
/// with these arguments" in tests without actually spawning a terminal, and
/// the concrete terminal/shell used is an implementation detail that can
/// change independently of callers.
/// </summary>
public interface ISessionLauncher
{
    /// <summary>
    /// Opens a new split pane in the user's terminal and resumes the given
    /// Copilot CLI session in it, verbatim (full history). Never reads or
    /// modifies any Copilot CLI state itself — it only starts a process;
    /// Copilot CLI's own <c>--resume</c>/<c>--continue</c> mechanism does
    /// the rest.
    /// </summary>
    /// <param name="sessionId">The session id to resume.</param>
    /// <param name="workingDirectory">
    /// The directory to launch the terminal pane in, if known (typically
    /// <see cref="Models.SessionSummary.Cwd"/>). Implementations should fall
    /// back to a reasonable default (e.g. the user's home directory) if null.
    /// </param>
    /// <param name="cancellationToken">Token to cancel before the process is launched.</param>
    Task ResumeInTerminalAsync(string sessionId, string? workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new split pane in the user's terminal and starts a brand new
    /// Copilot CLI session there, seeded only with <paramref name="summaryPrompt"/>
    /// as its first turn — not the original session's full history. Intended
    /// for long/old sessions where dragging the entire verbatim transcript
    /// into a new context window is more baggage than it's worth; callers
    /// typically build <paramref name="summaryPrompt"/> from the original
    /// session's most recent <see cref="Models.SessionCheckpoint.Overview"/>
    /// (Copilot CLI already produces these during a session — no separate
    /// LLM summarization call needed) or its <see cref="Models.SessionSummary.Summary"/>
    /// as a fallback. This deliberately creates a new, unrelated session id;
    /// it never reads or modifies the original session's own data.
    /// </summary>
    /// <param name="summaryPrompt">The summary text to seed the new session's first turn with.</param>
    /// <param name="workingDirectory">
    /// The directory to launch the terminal pane in, if known (typically
    /// <see cref="Models.SessionSummary.Cwd"/>). Implementations should fall
    /// back to a reasonable default (e.g. the user's home directory) if null.
    /// </param>
    /// <param name="cancellationToken">Token to cancel before the process is launched.</param>
    Task StartNewSessionFromSummaryAsync(string summaryPrompt, string? workingDirectory, CancellationToken cancellationToken = default);
}

