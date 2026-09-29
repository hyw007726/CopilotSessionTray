using System.Diagnostics;
using CopilotSessionTray.Core.Contracts;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="IProcessLivenessChecker"/>, backed by
/// <see cref="Process"/> — see IMPLEMENTATION_PLAN.md §2.5.
/// </summary>
public sealed class ProcessLivenessChecker : IProcessLivenessChecker
{
    public bool IsProcessRunning(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this id exists — it already exited, or never did.
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public IReadOnlyList<int> GetRunningCopilotProcessIds()
    {
        var processes = Process.GetProcessesByName("copilot");
        try
        {
            return processes.Select(p => p.Id).ToList();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
