namespace CopilotSessionTray.Core.Models;

/// <summary>
/// The outcome of a headless (non-interactive) Copilot CLI task run via
/// <see cref="Contracts.IYoloTaskRunner.RunHeadlessAsync"/>.
/// </summary>
/// <param name="Succeeded">Whether the process exited cleanly (exit code 0).</param>
/// <param name="Output">
/// Captured stdout. When the implementation runs with <c>--output-format=json</c>,
/// this is JSONL (one JSON object per line) suitable for parsing a verdict
/// out of, rather than free-form text.
/// </param>
/// <param name="ExitCode">The process's raw exit code.</param>
public sealed record YoloTaskResult(bool Succeeded, string Output, int ExitCode);
