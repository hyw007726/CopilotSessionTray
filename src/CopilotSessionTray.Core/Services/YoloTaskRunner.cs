using System.Diagnostics;
using System.Text;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="IYoloTaskRunner"/>.
///
/// <see cref="StartInteractiveAsync"/> is retrofitted per the 2026-09-29 design-review pass
/// (IMPLEMENTATION_PLAN.md §9.1): this logic previously lived directly as a private method on
/// <c>TrayViewModel</c> (<c>StartYoloTask</c>), bypassing the already-reviewed interface entirely.
///
/// <see cref="RunHeadlessAsync"/> is genuinely new — no prior code path exercised it. It shells out
/// <c>copilot -p &lt;prompt&gt; --allow-all --no-ask-user --output-format json</c> (flags confirmed
/// via <c>copilot --help</c>) with no visible window, capturing stdout for the caller to inspect,
/// e.g. to check whether another long-running session is genuinely stuck (IMPLEMENTATION_PLAN.md
/// §9's "stuck state machine" risk) without a human watching.
/// </summary>
public sealed class YoloTaskRunner : IYoloTaskRunner
{
    public Task StartInteractiveAsync(
        string prompt, string workingDirectory, bool allowAllPermissions = true, CancellationToken cancellationToken = default)
    {
        var sanitized = CopilotTerminalLauncher.SanitizeForCmdExe(prompt);
        CopilotTerminalLauncher.Launch($"-i \"{sanitized}\"", workingDirectory, allowAllPermissions);
        return Task.CompletedTask;
    }

    public async Task<YoloTaskResult> RunHeadlessAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
    {
        // Routed through "cmd.exe /c" rather than starting "copilot" directly: with
        // UseShellExecute=false (required to redirect stdout), Win32 CreateProcess only assumes a
        // ".exe" extension for a bare command name and does not consult PATHEXT the way a shell
        // does — so if the real "copilot" on PATH turns out to be an npm-style ".cmd" shim rather
        // than a raw .exe, starting it directly here would fail with "cannot find the file
        // specified" even though the exact same bare command works fine from an interactive
        // prompt. cmd.exe's own command resolution (used elsewhere in this app too, via
        // CopilotTerminalLauncher's generated scripts) handles either case correctly. Confirmed
        // by reproducing the direct-start failure against a ".cmd" stand-in before adding this.
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("copilot");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--allow-all");
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — kill the still-running headless process rather than leaving it
            // to finish unattended/unwatched after we've stopped caring about the result.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new YoloTaskResult(process.ExitCode == 0, output.ToString(), process.ExitCode);
    }
}
