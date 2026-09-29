namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Looks up which OS process (if any) currently holds the "inuse" lock for a given Copilot CLI
/// session, by reading <c>session-state\&lt;sessionId&gt;\inuse.&lt;pid&gt;.lock</c> — see
/// IMPLEMENTATION_PLAN.md §2.4.
///
/// Deliberately a plain concrete helper, <em>not</em> one of the 7 Core interfaces reviewed in
/// Phase 0.5: it doesn't cleanly belong to any of them (<see cref="Contracts.IOpenSessionsRegistryReader"/>
/// is scoped to <c>open-sessions-state.json</c> only), and this specific cross-referencing job is
/// really <see cref="Contracts.ISessionDetectionEngine"/>'s eventual responsibility once that's
/// implemented for real (with its own stateful poll-diffing). Kept here, small and clearly
/// labeled, as a minimal stand-in used only by the tray's live-data demo scenario — not something
/// unilaterally added to the reviewed contract surface.
/// </summary>
public sealed class SessionLockFileInspector
{
    private static readonly string SessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    /// <summary>
    /// Returns the process id embedded in this session's <c>inuse.&lt;pid&gt;.lock</c> file, or
    /// null if no such lock file currently exists (no live owning process on record for it).
    /// </summary>
    public int? GetOwningProcessId(string sessionId)
    {
        var sessionDir = Path.Combine(SessionStateRoot, sessionId);
        if (!Directory.Exists(sessionDir))
        {
            return null;
        }

        var lockFile = Directory.EnumerateFiles(sessionDir, "inuse.*.lock").FirstOrDefault();
        if (lockFile is null)
        {
            return null;
        }

        // Expected shape: inuse.<pid>.lock -> ["inuse", "<pid>", "lock"].
        var parts = Path.GetFileName(lockFile).Split('.');
        return parts.Length == 3 && int.TryParse(parts[1], out var pid) ? pid : null;
    }
}
