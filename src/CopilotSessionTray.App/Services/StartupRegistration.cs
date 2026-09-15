using Microsoft.Win32;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Registers/unregisters this app to launch at Windows login via the
/// per-user Run key. Distribution is xcopy/zip, not an installer (see
/// IMPLEMENTATION_PLAN.md §11), so a Registry Run-key entry is used rather
/// than a Start Menu/Task Scheduler based mechanism.
///
/// This is plain Windows/OS integration — it has nothing to do with reading
/// or interpreting any Copilot CLI state.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CopilotSessionTray";

    /// <summary>Whether a Run-key entry for this app currently exists.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string existing && !string.IsNullOrWhiteSpace(existing);
    }

    /// <summary>Creates or removes the Run-key entry for this app.</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
