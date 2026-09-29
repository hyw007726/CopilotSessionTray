using CopilotSessionTray.Core.Contracts;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="ISessionLauncher"/> — retrofitted per the 2026-09-29
/// design-review pass (IMPLEMENTATION_PLAN.md §9.1): this logic previously lived directly as
/// private methods on <c>TrayViewModel</c> (<c>LaunchCopilotInTerminal</c>), bypassing the
/// already-reviewed interface entirely. Moved here unchanged in behavior, just properly behind
/// the contract, and sharing <see cref="CopilotTerminalLauncher"/> with <see cref="YoloTaskRunner"/>.
/// </summary>
public sealed class SessionLauncher : ISessionLauncher
{
    public Task ResumeInTerminalAsync(string sessionId, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        CopilotTerminalLauncher.Launch($"--resume={sessionId}", workingDirectory);
        return Task.CompletedTask;
    }

    public Task StartNewSessionFromSummaryAsync(string summaryPrompt, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        var sanitized = CopilotTerminalLauncher.SanitizeForCmdExe(summaryPrompt);
        CopilotTerminalLauncher.Launch($"-i \"{sanitized}\"", workingDirectory);
        return Task.CompletedTask;
    }
}
