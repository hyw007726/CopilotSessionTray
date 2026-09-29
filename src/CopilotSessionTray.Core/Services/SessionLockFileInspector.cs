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
    private static readonly string DefaultSessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private readonly string _sessionStateRoot;

    /// <summary>
    /// Creates an inspector against the real <c>session-state</c> folder. An explicit
    /// <paramref name="sessionStateRoot"/> override exists solely so xUnit tests can point this at
    /// a temp fixture folder instead of the real <c>.copilot</c> folder — every real call site
    /// uses the parameterless default.
    /// </summary>
    public SessionLockFileInspector(string? sessionStateRoot = null)
    {
        _sessionStateRoot = sessionStateRoot ?? DefaultSessionStateRoot;
    }

    /// <summary>
    /// Returns the process id embedded in this session's <c>inuse.&lt;pid&gt;.lock</c> file, or
    /// null if no such lock file currently exists (no live owning process on record for it).
    ///
    /// Normally there's at most one lock file per session folder, but if a crashed process ever
    /// left a stale one behind before a new process created its own, more than one could
    /// technically coexist — <see cref="Directory.EnumerateFiles(string, string)"/> makes no
    /// ordering guarantee in that case, so picking the most recently written file (rather than
    /// whatever happens to enumerate first) keeps this deterministic and consistent with the
    /// same "freshest wins" rule <c>TrayViewModel.LoadLiveSessionsScenarioAsync</c> already
    /// applies when one pid holds locks across multiple session folders.
    /// </summary>
    public int? GetOwningProcessId(string sessionId)
    {
        var sessionDir = Path.Combine(_sessionStateRoot, sessionId);
        if (!Directory.Exists(sessionDir))
        {
            return null;
        }

        var lockFile = Directory.EnumerateFiles(sessionDir, "inuse.*.lock")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (lockFile is null)
        {
            return null;
        }

        // Expected shape: inuse.<pid>.lock -> ["inuse", "<pid>", "lock"].
        var parts = Path.GetFileName(lockFile).Split('.');
        return parts.Length == 3 && int.TryParse(parts[1], out var pid) ? pid : null;
    }
}
