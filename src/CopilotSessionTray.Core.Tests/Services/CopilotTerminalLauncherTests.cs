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
