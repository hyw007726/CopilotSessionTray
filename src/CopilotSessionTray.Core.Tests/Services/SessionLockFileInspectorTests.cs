using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="SessionLockFileInspector"/> against a throwaway temp folder (via its
/// testability constructor overload) standing in for <c>.copilot\session-state</c>. Includes a
/// regression test for the 2026-09-29 fix (IMPLEMENTATION_PLAN.md §9.1): picking the
/// most-recently-written lock file rather than an arbitrary <c>FirstOrDefault()</c> one when a
/// session folder holds more than one.
/// </summary>
public sealed class SessionLockFileInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lockfile-tests-{Guid.NewGuid():N}");

    public SessionLockFileInspectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionLockFileInspector CreateInspector() => new(_root);

    [Fact]
    public void GetOwningProcessId_ReturnsNull_WhenSessionFolderDoesNotExist()
    {
        var inspector = CreateInspector();

        Assert.Null(inspector.GetOwningProcessId("does-not-exist"));
    }

    [Fact]
    public void GetOwningProcessId_ReturnsNull_WhenSessionFolderHasNoLockFile()
    {
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);

        var inspector = CreateInspector();

        Assert.Null(inspector.GetOwningProcessId("session-1"));
    }

    [Fact]
    public void GetOwningProcessId_ReturnsPid_FromSingleLockFile()
    {
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "inuse.4242.lock"), string.Empty);

        var inspector = CreateInspector();

        Assert.Equal(4242, inspector.GetOwningProcessId("session-1"));
    }

    [Fact]
    public void GetOwningProcessId_IgnoresUnrelatedFiles_InTheSameFolder()
    {
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "workspace.yaml"), "cwd: C:\\repo");
        File.WriteAllText(Path.Combine(sessionDir, "inuse.777.lock"), string.Empty);

        var inspector = CreateInspector();

        Assert.Equal(777, inspector.GetOwningProcessId("session-1"));
    }

    [Fact]
    public void GetOwningProcessId_ReturnsNull_WhenLockFileNameIsMalformed()
    {
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "inuse.not-a-number.lock"), string.Empty);

        var inspector = CreateInspector();

        Assert.Null(inspector.GetOwningProcessId("session-1"));
    }

    [Fact]
    public void GetOwningProcessId_PicksMostRecentlyWrittenLockFile_WhenMultipleExist()
    {
        // Regression test for the 2026-09-29 fix: a crashed process's stale lock file must not
        // win over a newer, real one just because of arbitrary filesystem enumeration order.
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);

        var stalePath = Path.Combine(sessionDir, "inuse.1111.lock");
        var freshPath = Path.Combine(sessionDir, "inuse.2222.lock");
        File.WriteAllText(stalePath, string.Empty);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(freshPath, string.Empty);
        File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow);

        var inspector = CreateInspector();

        Assert.Equal(2222, inspector.GetOwningProcessId("session-1"));
    }

    [Fact]
    public void GetOwningProcessId_PicksMostRecentlyWrittenLockFile_RegardlessOfWhichWasCreatedFirstOnDisk()
    {
        // Same as above but with creation order reversed relative to write-time, to make sure the
        // fix keys off last-write-time and not e.g. file creation order/name sort.
        var sessionDir = Path.Combine(_root, "session-1");
        Directory.CreateDirectory(sessionDir);

        var freshPath = Path.Combine(sessionDir, "inuse.9999.lock"); // higher pid, created first
        var stalePath = Path.Combine(sessionDir, "inuse.1000.lock"); // lower pid, created second
        File.WriteAllText(freshPath, string.Empty);
        File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow);
        File.WriteAllText(stalePath, string.Empty);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-2));

        var inspector = CreateInspector();

        Assert.Equal(9999, inspector.GetOwningProcessId("session-1"));
    }
}
