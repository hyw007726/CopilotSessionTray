using System.Diagnostics;
using System.Text;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="CopilotTerminalLauncher"/> — script-content building and cmd.exe-safe
/// sanitization — and, for the argument-delivery tests, runs the generated script for real against
/// a stub <c>copilot.cmd</c>. Accessible here via <c>InternalsVisibleTo</c> (see Core.csproj)
/// since these are deliberately not part of Core's public API.
/// </summary>
public sealed class CopilotTerminalLauncherTests
{
    [Fact]
    public void BuildLaunchScriptContent_OmitsEnvSetup_WhenPermissionsNotEnabled()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("--resume=abc123", enableAllPermissions: false, "1a1a2e");

        Assert.DoesNotContain("COPILOT_ALLOW_ALL", content);
        Assert.Contains("copilot --resume=abc123", content);
        Assert.StartsWith("@echo off", content);
    }

    [Fact]
    public void BuildLaunchScriptContent_IncludesEnvSetup_WhenPermissionsEnabled()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("-i \"do the thing\"", enableAllPermissions: true, "1a1a2e");

        Assert.Contains("set COPILOT_ALLOW_ALL=true", content);
        Assert.Contains("copilot -i \"do the thing\"", content);
        // The env-setup line must come before the copilot invocation for it to take effect.
        Assert.True(content.IndexOf("COPILOT_ALLOW_ALL", StringComparison.Ordinal)
            < content.IndexOf("copilot -i", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLaunchScriptContent_IncludesBackgroundColorEscapeSequence_BeforeTheCopilotInvocation()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("--resume=abc123", enableAllPermissions: false, "3d0e0e");

        Assert.Contains("\u001b]11;#3d0e0e\u0007", content);
        Assert.True(content.IndexOf('\u001b') < content.IndexOf("copilot --resume", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLaunchScriptContent_SwitchesToTheUtf8CodePage_BeforeTheCopilotInvocation()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("--resume=abc123", enableAllPermissions: false, "1a1a2e");

        var codePageIndex = content.IndexOf("chcp 65001 >nul", StringComparison.Ordinal);
        Assert.True(codePageIndex > content.IndexOf("@echo off", StringComparison.Ordinal));
        Assert.True(codePageIndex < content.IndexOf("copilot --resume", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedLaunchScript_DeliversNonAsciiArgumentsIntact_EvenFromAnOemCodePageConsole()
    {
        const string text = "em dash — ellipsis … middle dot · accent é cjk 日本 emoji 🙂";

        var arguments = RunGeneratedLaunchScriptAgainstStubCopilot($"-i \"{text}\"");

        Assert.Equal(new[] { "-i", text }, arguments);
    }

    [Fact]
    public void GeneratedLaunchScript_DeliversPercentSignsLiterally_ForSanitizedText()
    {
        const string text = "has 100% coverage, %PATH% and %1 and %~dp0 stay literal";

        var arguments = RunGeneratedLaunchScriptAgainstStubCopilot($"-i \"{CopilotTerminalLauncher.SanitizeForCmdExe(text)}\"");

        Assert.Equal(new[] { "-i", text }, arguments);
    }

    [Fact]
    public void BuildBackgroundColorEscapeSequence_ProducesOsc11SequenceWithBelTerminator()
    {
        var sequence = CopilotTerminalLauncher.BuildBackgroundColorEscapeSequence("1a1a2e");

        Assert.Equal("\u001b]11;#1a1a2e\u0007", sequence);
    }

    [Fact]
    public void GetNextBackgroundColorHex_ReturnsADifferentColor_OnTheImmediatelyNextCall()
    {
        // Regardless of this static rotation's current position (shared mutable state across the
        // whole test run/process, including real app usage), two *consecutive* calls must always
        // land on adjacent — therefore different — palette slots.
        var first = CopilotTerminalLauncher.GetNextBackgroundColorHex();
        var second = CopilotTerminalLauncher.GetNextBackgroundColorHex();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetNextBackgroundColorHex_AlwaysReturnsValidLowercaseHexWithNoHash()
    {
        var color = CopilotTerminalLauncher.GetNextBackgroundColorHex();

        Assert.Matches("^[0-9a-f]{6}$", color);
    }

    [Fact]
    public void GetNextBackgroundColorHex_CyclingThroughAFullPaletteLength_NeverRepeatsWithinThatSpan()
    {
        // Doesn't assume any particular starting index (see the note on shared static state
        // above) — only that N consecutive calls, for N equal to the palette's own size, can
        // never repeat a color within that single span, since each call always advances exactly
        // one slot around a fixed-size ring of all-distinct entries.
        const int paletteLength = 14; // kept in sync with BackgroundColorPalette's literal size.
        var colors = new HashSet<string>();
        for (var i = 0; i < paletteLength; i++)
        {
            colors.Add(CopilotTerminalLauncher.GetNextBackgroundColorHex());
        }

        Assert.Equal(paletteLength, colors.Count);
    }

    [Theory]
    [InlineData("simple text", "simple text")]
    [InlineData("has \"quotes\" inside", "has 'quotes' inside")]
    [InlineData("has 100% coverage", "has 100%% coverage")]
    [InlineData("line one\r\nline two", "line one  line two")]
    [InlineData("line one\nline two", "line one line two")]
    public void SanitizeForCmdExe_ReplacesUnsafeCharacters(string input, string expected)
    {
        Assert.Equal(expected, CopilotTerminalLauncher.SanitizeForCmdExe(input));
    }

    [Fact]
    public void SanitizeForCmdExe_LeavesSafeTextUnchanged()
    {
        const string safe = "Investigate the flaky retry logic in checkout, then report back.";

        Assert.Equal(safe, CopilotTerminalLauncher.SanitizeForCmdExe(safe));
    }

    private static string[] RunGeneratedLaunchScriptAgainstStubCopilot(string copilotArguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"copilot-launch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var capturePath = Path.Combine(directory, "captured.txt");
            var capturePs1Path = Path.Combine(directory, "capture.ps1");
            var scriptPath = Path.Combine(directory, "launch.cmd");
            var wrapperPath = Path.Combine(directory, "wrapper.cmd");

            File.WriteAllText(
                capturePs1Path,
                $"[System.IO.File]::WriteAllText('{capturePath.Replace("'", "''")}', ($args -join \"`n\"), (New-Object System.Text.UTF8Encoding($false)))");
            File.WriteAllText(
                Path.Combine(directory, "copilot.cmd"),
                $"@echo off\r\npowershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{capturePs1Path}\" %*\r\n");
            File.WriteAllText(
                scriptPath,
                CopilotTerminalLauncher.BuildLaunchScriptContent(copilotArguments, enableAllPermissions: false, "1a1a2e"));
            File.WriteAllText(
                wrapperPath,
                $"@echo off\r\nchcp 850 >nul\r\nset \"PATH={directory};%PATH%\"\r\ncall \"{scriptPath}\"\r\n");

            var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"\"{wrapperPath}\"\"")
            {
                WorkingDirectory = directory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo)!;
            Assert.True(process.WaitForExit(60_000), "The generated launch script did not finish.");

            return File.ReadAllText(capturePath, Encoding.UTF8).Split('\n');
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
