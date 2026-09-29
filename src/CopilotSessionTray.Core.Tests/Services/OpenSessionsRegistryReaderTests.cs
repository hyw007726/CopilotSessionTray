using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="OpenSessionsRegistryReader"/> against a throwaway temp fixture file (via
/// its testability constructor overload) standing in for the real
/// <c>open-sessions-state.json</c>, using the exact shape observed on a real machine (see
/// IMPLEMENTATION_PLAN.md §2.3).
/// </summary>
public sealed class OpenSessionsRegistryReaderTests : IDisposable
{
    private readonly string _registryFilePath = Path.Combine(Path.GetTempPath(), $"registry-tests-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_registryFilePath))
        {
            File.Delete(_registryFilePath);
        }
    }

    private OpenSessionsRegistryReader CreateReader() => new(_registryFilePath);

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenFileDoesNotExist()
    {
        IOpenSessionsRegistryReader reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadAsync_ParsesRealObservedShape_IncludingCamelCaseFields()
    {
        await File.WriteAllTextAsync(_registryFilePath, """
            {
              "abc-123": {
                "schemaVersion": 1,
                "openedAt": "2026-09-14T09:45:44.355Z",
                "working": true,
                "refreshedAt": "2026-09-14T09:47:56.369Z"
              }
            }
            """);
        IOpenSessionsRegistryReader reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Single(result);
        var entry = result["abc-123"];
        Assert.Equal("abc-123", entry.SessionId);
        Assert.Equal(1, entry.SchemaVersion);
        Assert.True(entry.Working);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T09:45:44.355Z"), entry.OpenedAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T09:47:56.369Z"), entry.RefreshedAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ParsesMultipleEntries_KeyedBySessionId()
    {
        await File.WriteAllTextAsync(_registryFilePath, """
            {
              "session-a": { "schemaVersion": 1, "openedAt": "2026-01-01T00:00:00Z", "working": true, "refreshedAt": "2026-01-01T00:01:00Z" },
              "session-b": { "schemaVersion": 1, "openedAt": "2026-01-02T00:00:00Z", "working": false, "refreshedAt": "2026-01-02T00:01:00Z" }
            }
            """);
        IOpenSessionsRegistryReader reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Equal(2, result.Count);
        Assert.True(result["session-a"].Working);
        Assert.False(result["session-b"].Working);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenFileIsAnEmptyJsonObject()
    {
        await File.WriteAllTextAsync(_registryFilePath, "{}");
        IOpenSessionsRegistryReader reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenFileStaysInvalidJsonAfterRetries()
    {
        // Simulates a write that never lands correctly (as opposed to a transient mid-write
        // read) — the interface's documented contract is to retry briefly, then surface failure
        // rather than silently returning an empty/wrong result.
        await File.WriteAllTextAsync(_registryFilePath, "{ not valid json at all");
        IOpenSessionsRegistryReader reader = CreateReader();

        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync());
    }
}
