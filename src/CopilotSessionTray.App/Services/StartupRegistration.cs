using System.Diagnostics;
using System.IO;
using System.Text;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Registers/unregisters this app to launch at Windows login via a per-user Scheduled Task —
/// chosen over a plain Registry Run-key entry or Startup-folder shortcut (2026-10-01 revision;
/// see IMPLEMENTATION_PLAN.md §9.1/§10 Phase 5) specifically so a crash can be auto-recovered:
/// the task's <c>RestartOnFailure</c> setting relaunches the app automatically when it exits with
/// a non-zero/crash exit code, while an intentional "Quit" (clean exit, code 0) is correctly left
/// alone. A Run key / shortcut only ever fires once, at logon, with no such recovery.
///
/// Implemented via <c>schtasks.exe /Create /XML</c> rather than a Task Scheduler NuGet dependency
/// or raw COM interop, consistent with this app's existing pattern (see
/// <see cref="CopilotSessionTray.Core.Services.CopilotTerminalLauncher"/>) of shelling out to a
/// built-in Windows executable rather than adding a dependency for OS integration. The task runs
/// entirely under the current user's own token, <c>RunLevel=LeastPrivilege</c> — no admin
/// elevation needed (verified empirically), matching the xcopy/zip, no-installer distribution
/// model (§11).
///
/// This is plain Windows/OS integration — it has nothing to do with reading or interpreting any
/// Copilot CLI state.
/// </summary>
internal static class StartupRegistration
{
    private const string TaskName = "CopilotSessionTray";

    /// <summary>Whether a Scheduled Task entry for this app currently exists.</summary>
    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    /// <summary>Creates or removes the Scheduled Task entry for this app.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"CopilotSessionTray-task-{Guid.NewGuid():N}.xml");
        try
        {
            // UTF-16 ("Unicode") required: Task Scheduler's XML importer rejects UTF-8 task
            // definitions outright (confirmed empirically), despite the XML declaration being
            // generic — it only honors the file's actual on-disk encoding.
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);
            RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    /// <summary>
    /// Builds the Task Scheduler XML definition — split out purely so it can be unit-tested
    /// without actually touching the real Task Scheduler. Key settings:
    /// <see cref="CopilotSessionTray.Core.Services.CopilotTerminalLauncher"/>-style, this is pure
    /// string-building logic only.
    /// <list type="bullet">
    /// <item><c>LogonTrigger</c>: fires at this user's own logon only (not "any user").</item>
    /// <item><c>LogonType=InteractiveToken</c>/<c>RunLevel=LeastPrivilege</c>: runs in the
    /// interactive desktop session (required for a WPF tray icon to be visible at all) without
    /// admin elevation.</item>
    /// <item><c>ExecutionTimeLimit=PT0S</c>: "no time limit" — without this, Task Scheduler's
    /// default (commonly 72 hours) would forcibly kill this long-running tray app.</item>
    /// <item><c>RestartOnFailure</c> (interval 1 minute, up to 999 times): the actual crash-recovery
    /// behavior this revision exists for.</item>
    /// <item><c>MultipleInstancesPolicy=IgnoreNew</c>: a side-benefit, not a full fix for the
    /// still-open "single-instance guard" gap (§9.1) — only prevents Task Scheduler itself from
    /// double-launching this task, not a manually-started second copy.</item>
    /// <item>Battery settings left permissive (<c>DisallowStartIfOnBatteries</c>/
    /// <c>StopIfGoingOnBatteries</c> both <c>false</c>): a personal monitoring tool should keep
    /// running on a laptop running on battery, same as today's Run-key behavior did.</item>
    /// </list>
    /// </summary>
    internal static string BuildTaskXml(string exePath)
    {
        var userId = $@"{Environment.UserDomainName}\{Environment.UserName}";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userId}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userId}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exePath}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
