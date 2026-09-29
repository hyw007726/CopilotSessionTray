using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises the pure-logic parts of <see cref="CopilotTerminalLauncher"/> — script-content
/// building and cmd.exe-safe sanitization — without ever writing a file or starting a process.
/// Accessible here via <c>InternalsVisibleTo</c> (see Core.csproj) since these are deliberately
/// not part of Core's public API.
/// </summary>
public sealed class CopilotTerminalLauncherTests
{
    [Fact]
    public void BuildLaunchScriptContent_OmitsEnvSetup_WhenPermissionsNotEnabled()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("--resume=abc123", enableAllPermissions: false);

        Assert.DoesNotContain("COPILOT_ALLOW_ALL", content);
        Assert.Contains("copilot --resume=abc123", content);
        Assert.StartsWith("@echo off", content);
    }

    [Fact]
    public void BuildLaunchScriptContent_IncludesEnvSetup_WhenPermissionsEnabled()
    {
        var content = CopilotTerminalLauncher.BuildLaunchScriptContent("-i \"do the thing\"", enableAllPermissions: true);

        Assert.Contains("set COPILOT_ALLOW_ALL=true", content);
        Assert.Contains("copilot -i \"do the thing\"", content);
        // The env-setup line must come before the copilot invocation for it to take effect.
        Assert.True(content.IndexOf("COPILOT_ALLOW_ALL", StringComparison.Ordinal)
            < content.IndexOf("copilot -i", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("simple text", "simple text")]
    [InlineData("has \"quotes\" inside", "has 'quotes' inside")]
    [InlineData("has 100% coverage", "has 100_ coverage")]
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
}
