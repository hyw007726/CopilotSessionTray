namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Launches an unattended ("yolo") Copilot CLI task — a run started with
/// full permissions (<c>--allow-all</c>/<c>--yolo</c>), so it doesn't stop
/// to ask before using tools/paths/URLs — for auxiliary jobs the app wants
/// Copilot to do on its own behalf, as distinct from
/// <see cref="ISessionLauncher"/>, which opens an interactive pane purely
/// for a human to drive. Like <see cref="ISessionLauncher"/>, this is a
/// real, user-visible/user-impacting side effect (spawning a process with
/// no per-action confirmation), so it gets its own interface for the same
/// reason: callers can fake or record "a yolo task was requested with these
/// arguments" in tests without actually running one.
/// </summary>
/// <remarks>
/// Two shapes, matching two different intended uses:
/// <list type="bullet">
/// <item>
/// <see cref="StartInteractiveAsync"/> — for a human-initiated task (e.g. a
/// "start new task" panel: prompt + workspace picker): opens a visible
/// terminal pane seeded with the prompt and lets the user watch/keep
/// chatting. Fire-and-forget from the app's point of view.
/// </item>
/// <item>
/// <see cref="RunHeadlessAsync"/> — for an app-initiated task with no human
/// watching (e.g. investigating whether another long-running session looks
/// genuinely stuck rather than just slow — see IMPLEMENTATION_PLAN.md §9's
/// "stuck state machine" risk): runs to completion in the background and
/// returns a result to inspect programmatically.
/// </item>
/// </list>
/// Neither method reads or modifies any other Copilot CLI session's own
/// state — each starts a brand new, independent session.
/// </remarks>
public interface IYoloTaskRunner
{
    /// <summary>
    /// Opens a new split terminal pane and starts an unattended Copilot CLI
    /// session there, seeded with <paramref name="prompt"/>, for the user to
    /// watch or continue chatting with.
    /// </summary>
    /// <param name="prompt">The task prompt to seed the new session with.</param>
    /// <param name="workingDirectory">The workspace/repo folder to run in.</param>
    /// <param name="cancellationToken">Token to cancel before the process is launched.</param>
    Task StartInteractiveAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an unattended, non-interactive Copilot CLI task
    /// (<c>-p</c>/<c>--no-ask-user</c>/<c>--output-format=json</c>) to
    /// completion in the background, with no visible terminal, and returns
    /// its result.
    /// </summary>
    /// <param name="prompt">The task prompt to run.</param>
    /// <param name="workingDirectory">The workspace/repo folder to run in.</param>
    /// <param name="cancellationToken">Token to cancel/kill the underlying process early.</param>
    Task<Models.YoloTaskResult> RunHeadlessAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default);
}
