using System.Diagnostics;
using CopilotSessionTray.Core.Models;

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
    /// Builds the OSC 11 VT sequence that sets a terminal pane's background color dynamically,
    /// for the life of that pane only (confirmed supported natively by Windows Terminal, which
    /// this app's launch path always goes through). Chosen over any <c>wt.exe</c> command-line
    /// flag (e.g. <c>--colorScheme</c>, which needs a scheme already named in <c>settings.json</c>)
    /// or editing the user's own <c>settings.json</c> directly: an inline VT sequence needs no
    /// pre-registered scheme/profile and touches nothing outside this generated script, matching
    /// this app's existing rule of never modifying anything beyond its own scope. BEL (<c>\a</c>)
    /// terminator used rather than the two-byte ST (<c>ESC \</c>) form — both are valid per the
    /// OSC spec and Windows Terminal accepts either, so the simpler single-byte form is used.
    /// </summary>
    /// <param name="backgroundColor">The color, as <c>#RRGGBB</c>.</param>
    internal static string BuildBackgroundColorEscapeSequence(string backgroundColor) =>
        $"\u001b]11;{backgroundColor}\u0007";

    /// <summary>
    /// Writes the launch script's content — split out from <see cref="Launch"/> purely so it can
    /// be unit-tested without actually writing a file or starting a process.
    /// </summary>
    internal static string BuildLaunchScriptContent(string copilotArguments, bool enableAllPermissions, string backgroundColor)
    {
        var envSetupLine = enableAllPermissions ? $"set COPILOT_ALLOW_ALL=true{Environment.NewLine}" : string.Empty;
        // "echo <raw escape bytes>" works here because cmd.exe's echo passes its argument text
        // through to stdout verbatim (no backslash-escape interpretation like a Unix shell); the
        // actual VT-sequence parsing happens one layer up, in Windows Terminal itself, which
        // inspects the whole output byte stream regardless of which program (cmd.exe's own echo,
        // in this case) produced it.
        var backgroundColorLine = $"echo {BuildBackgroundColorEscapeSequence(backgroundColor)}{Environment.NewLine}";
        return $"@echo off{Environment.NewLine}chcp 65001 >nul{Environment.NewLine}{backgroundColorLine}{envSetupLine}copilot {copilotArguments}{Environment.NewLine}";
    }

    /// <summary>
    /// Builds the <c>wt.exe</c> arguments that open the launch script in a new tab with the given
    /// tab color — split out from <see cref="Launch"/> so they can be unit-tested without starting
    /// a process.
    /// </summary>
    /// <param name="workingDirectory">The directory the terminal starts in.</param>
    /// <param name="scriptPath">The generated launch script.</param>
    /// <param name="tabColor">The color of the tab, as <c>#RRGGBB</c>.</param>
    internal static IReadOnlyList<string> BuildWindowsTerminalArguments(string workingDirectory, string scriptPath, string tabColor) =>
        ["new-tab", "--tabColor", tabColor, "-d", workingDirectory, "cmd", "/k", scriptPath];

    /// <summary>
    /// Makes arbitrary text safe to embed inside a single quoted argument on a generated
    /// batch-file command line: double quotes become single quotes, <c>%</c> is doubled (the
    /// batch-file escape, so it reaches <c>copilot</c> as a literal <c>%</c>), and line breaks
    /// become spaces. Adequate for a demo/summary string; feeding arbitrary untrusted text should
    /// instead use something injection-proof like PowerShell's base64 <c>-EncodedCommand</c>.
    /// </summary>
    internal static string SanitizeForCmdExe(string text) =>
        text.Replace('"', '\'').Replace("%", "%%").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Writes the launch script and works out the <c>wt.exe</c> arguments that open it — split out
    /// from <see cref="Launch"/> so everything but starting the process can be unit-tested.
    /// </summary>
    internal static (string ScriptPath, IReadOnlyList<string> Arguments) PrepareLaunch(
        string copilotArguments, string? workingDirectory, TerminalColor color, bool enableAllPermissions)
    {
        var resolvedWorkingDirectory = workingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Left in %TEMP% rather than deleted right after launch: cmd /k keeps running (and could
        // still need to re-read the file) for as long as the pane stays open, which has no fixed
        // end time — a stray handful of tiny leftover .cmd files is an acceptable trade-off for a
        // personal tool versus the complexity of tracking pane lifetime.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"copilot-launch-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, BuildLaunchScriptContent(copilotArguments, enableAllPermissions, color.Background));

        return (scriptPath, BuildWindowsTerminalArguments(resolvedWorkingDirectory, scriptPath, color.Accent));
    }

    /// <summary>
    /// Opens a new Windows Terminal window/pane running <c>copilot &lt;copilotArguments&gt;</c> in
    /// <paramref name="workingDirectory"/> (falling back to the user's home directory if null),
    /// with a tab and a pane background in <paramref name="color"/>.
    /// </summary>
    public static void Launch(string copilotArguments, string? workingDirectory, TerminalColor color, bool enableAllPermissions = false)
    {
        var (_, arguments) = PrepareLaunch(copilotArguments, workingDirectory, color, enableAllPermissions);

        var startInfo = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        // ArgumentList (not a single interpolated Arguments string) is used here so embedded
        // spaces in the working directory or script path can't be misread as extra wt.exe-level
        // arguments — each item becomes exactly one properly-quoted Win32 argument regardless of
        // its contents.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }
}
