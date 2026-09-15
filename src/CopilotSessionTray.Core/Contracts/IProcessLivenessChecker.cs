namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Wraps OS process lookups used to confirm whether a Copilot CLI session's
/// owning process is actually still alive (an <c>inuse.&lt;pid&gt;.lock</c>
/// file or a <c>working: true</c> registry entry can be stale if the
/// process crashed without cleaning up).
/// </summary>
public interface IProcessLivenessChecker
{
    /// <summary>Returns whether a process with the given id is currently running.</summary>
    /// <param name="processId">The OS process id to check.</param>
    bool IsProcessRunning(int processId);

    /// <summary>Returns the process ids of all currently running Copilot CLI (<c>copilot</c>) processes.</summary>
    IReadOnlyList<int> GetRunningCopilotProcessIds();
}
