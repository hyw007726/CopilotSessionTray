using System.Diagnostics;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Shared "open a visible terminal pane running <c>copilot ...</c>" logic behind
/// <see cref="SessionLauncher"/> and <see cref="YoloTaskRunner"/>'s
/// <see cref="Contracts.IYoloTaskRunner.StartInteractiveAsync"/> — factored out so the two
/// interfaces' real implementations share one tested path instead of duplicating it. Originally
/// lived directly in <c>TrayViewModel</c> before being retrofitted behind these interfaces; see
/// IMPLEMENTATION_PLAN.md §9.1/§10 Phase 2 for the empirically-found fixes baked into this
/// (single-pane <c>wt.exe</c> invocation, temp-script quoting, <c>COPILOT_ALLOW_ALL</c>).
///
/// Deliberately has no UI dependency (no <c>MessageBox</c>) — <see cref="Process.Start(ProcessStartInfo)"/>
/// throwing <see cref="System.ComponentModel.Win32Exception"/> on failure (e.g. Windows Terminal
/// not installed) is left to propagate to the caller, which decides how to surface it.
/// </summary>
internal static class CopilotTerminalLauncher
{
    /// <summary>
    /// Writes the launch script's content — split out from <see cref="Launch"/> purely so it can
    /// be unit-tested without actually writing a file or starting a process.
    /// </summary>
    internal static string BuildLaunchScriptContent(string copilotArguments, bool enableAllPermissions)
    {
        var envSetupLine = enableAllPermissions ? $"set COPILOT_ALLOW_ALL=true{Environment.NewLine}" : string.Empty;
        return $"@echo off{Environment.NewLine}{envSetupLine}copilot {copilotArguments}{Environment.NewLine}";
    }

    /// <summary>
    /// Makes arbitrary text safe to embed inside a single quoted argument on a generated
    /// batch-file command line. cmd.exe has no fully reliable, universal escape for embedded
    /// double-quotes or <c>%</c> (variable expansion) in that position, so rather than fight its
    /// notoriously inconsistent quoting, this replaces the characters that would otherwise break
    /// or alter the command with safe look-alikes. Adequate for a demo/summary string; feeding
    /// arbitrary untrusted text should instead use something injection-proof like PowerShell's
    /// base64 <c>-EncodedCommand</c>.
    /// </summary>
    internal static string SanitizeForCmdExe(string text) =>
        text.Replace('"', '\'').Replace('%', '_').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Opens a new Windows Terminal window/pane running <c>copilot &lt;copilotArguments&gt;</c> in
    /// <paramref name="workingDirectory"/> (falling back to the user's home directory if null).
    /// </summary>
    public static void Launch(string copilotArguments, string? workingDirectory, bool enableAllPermissions = false)
    {
        var resolvedWorkingDirectory = workingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Left in %TEMP% rather than deleted right after launch: cmd /k keeps running (and could
        // still need to re-read the file) for as long as the pane stays open, which has no fixed
        // end time — a stray handful of tiny leftover .cmd files is an acceptable trade-off for a
        // personal tool versus the complexity of tracking pane lifetime.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"copilot-launch-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, BuildLaunchScriptContent(copilotArguments, enableAllPermissions));

        var startInfo = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        // ArgumentList (not a single interpolated Arguments string) is used here so embedded
        // spaces in the working directory or script path can't be misread as extra wt.exe-level
        // arguments — each item becomes exactly one properly-quoted Win32 argument regardless of
        // its contents.
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(resolvedWorkingDirectory);
        startInfo.ArgumentList.Add("cmd");
        startInfo.ArgumentList.Add("/k");
        startInfo.ArgumentList.Add(scriptPath);

        Process.Start(startInfo);
    }
}
