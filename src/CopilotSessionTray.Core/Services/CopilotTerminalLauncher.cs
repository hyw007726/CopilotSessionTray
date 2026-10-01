using System.Diagnostics;
using System.Threading;

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
    /// Curated palette of visually-distinct, dark background colors (hex, no leading <c>#</c>) —
    /// added 2026-10-01 per explicit user request: each new terminal this app opens gets the next
    /// color here (see <see cref="GetNextBackgroundColorHex"/>), so several concurrently-open
    /// panes are easy to tell apart at a glance instead of all looking identical. All chosen dark
    /// enough that the default white/light-gray terminal foreground text stays legible against
    /// every one of them, while still spanning clearly different hues (navy/teal/maroon/green/
    /// purple/etc.) rather than just different shades of the same dark gray.
    /// </summary>
    private static readonly string[] BackgroundColorPalette =
    [
        "1a1a2e", // dark navy
        "0f3d3e", // dark teal
        "3d0e0e", // dark maroon
        "1b3a1b", // dark forest green
        "2e1a47", // dark purple
        "3a2a1a", // dark brown
        "0d2b45", // dark steel blue
        "4a1942", // dark magenta
        "203a43", // dark slate cyan
        "4a2c0a", // dark burnt orange
        "1f2d3d", // dark blue-gray
        "3d1a1a", // dark brick red
        "163020", // dark pine green
        "2d132c", // dark plum
    ];

    private static int _nextColorIndex = -1;

    /// <summary>
    /// Returns the next color (hex, no <c>#</c>) from <see cref="BackgroundColorPalette"/>,
    /// round-robin. <see cref="Interlocked.Increment(ref int)"/>-based (not just <c>++</c>) so two
    /// terminals launched back-to-back (e.g. two quick "Resume" clicks) can never race onto the
    /// same index. Wraps back to the start once the palette is exhausted — harmless, since by
    /// then the earliest-colored panes are typically long closed.
    /// </summary>
    internal static string GetNextBackgroundColorHex()
    {
        var index = Interlocked.Increment(ref _nextColorIndex);
        return BackgroundColorPalette[(int)((uint)index % BackgroundColorPalette.Length)];
    }

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
    internal static string BuildBackgroundColorEscapeSequence(string backgroundColorHex) =>
        $"\u001b]11;#{backgroundColorHex}\u0007";

    /// <summary>
    /// Writes the launch script's content — split out from <see cref="Launch"/> purely so it can
    /// be unit-tested without actually writing a file or starting a process.
    /// </summary>
    internal static string BuildLaunchScriptContent(string copilotArguments, bool enableAllPermissions, string backgroundColorHex)
    {
        var envSetupLine = enableAllPermissions ? $"set COPILOT_ALLOW_ALL=true{Environment.NewLine}" : string.Empty;
        // "echo <raw escape bytes>" works here because cmd.exe's echo passes its argument text
        // through to stdout verbatim (no backslash-escape interpretation like a Unix shell); the
        // actual VT-sequence parsing happens one layer up, in Windows Terminal itself, which
        // inspects the whole output byte stream regardless of which program (cmd.exe's own echo,
        // in this case) produced it.
        var backgroundColorLine = $"echo {BuildBackgroundColorEscapeSequence(backgroundColorHex)}{Environment.NewLine}";
        return $"@echo off{Environment.NewLine}{backgroundColorLine}{envSetupLine}copilot {copilotArguments}{Environment.NewLine}";
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

        var backgroundColorHex = GetNextBackgroundColorHex();

        // Left in %TEMP% rather than deleted right after launch: cmd /k keeps running (and could
        // still need to re-read the file) for as long as the pane stays open, which has no fixed
        // end time — a stray handful of tiny leftover .cmd files is an acceptable trade-off for a
        // personal tool versus the complexity of tracking pane lifetime.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"copilot-launch-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, BuildLaunchScriptContent(copilotArguments, enableAllPermissions, backgroundColorHex));

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
