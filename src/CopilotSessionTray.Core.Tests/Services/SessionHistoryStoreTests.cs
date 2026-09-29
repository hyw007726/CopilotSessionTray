using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Services;
using Microsoft.Data.Sqlite;

namespace CopilotSessionTray.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="SessionHistoryStore"/> against a throwaway temp SQLite database (via its
/// testability constructor overload) built with the same table/column shape as the real
/// <c>session-store.db</c> (see IMPLEMENTATION_PLAN.md §2.2) — never the developer's own real,
/// populated database.
/// </summary>
public sealed class SessionHistoryStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"history-tests-{Guid.NewGuid():N}.db");

    public SessionHistoryStoreTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, cwd TEXT, repository TEXT, host_type TEXT, summary TEXT,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE turns (
                id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, turn_index INTEGER NOT NULL,
                user_message TEXT, assistant_response TEXT, timestamp TEXT NOT NULL);
            CREATE TABLE checkpoints (
                id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, checkpoint_number INTEGER NOT NULL,
                title TEXT, overview TEXT, created_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release the file handle before deleting on Windows.
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SessionHistoryStore CreateStore() => new(_databasePath);

    private void InsertSession(string id, string? cwd, string? repository, string? summary, string createdAt, string updatedAt)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO sessions (id, cwd, repository, host_type, summary, created_at, updated_at) " +
            "VALUES ($id, $cwd, $repository, 'cli', $summary, $createdAt, $updatedAt)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$cwd", (object?)cwd ?? DBNull.Value);
        command.Parameters.AddWithValue("$repository", (object?)repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetRecentSessionsAsync_ReturnsEmpty_WhenNoSessionsExist()
    {
        ISessionHistoryStore store = CreateStore();

        var result = await store.GetRecentSessionsAsync(maxCount: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecentSessionsAsync_OrdersByUpdatedAtDescending()
    {
        InsertSession("older", "C:\\a", "acme/a", "older", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z");
        InsertSession("newer", "C:\\b", "acme/b", "newer", "2026-01-02T00:00:00Z", "2026-01-03T00:00:00Z");
        ISessionHistoryStore store = CreateStore();

        var result = await store.GetRecentSessionsAsync(maxCount: 10);

        Assert.Equal(["newer", "older"], result.Select(s => s.Id));
    }

    [Fact]
    public async Task GetRecentSessionsAsync_RespectsMaxCount()
    {
        for (var i = 0; i < 5; i++)
        {
            InsertSession($"session-{i}", null, null, null, "2026-01-01T00:00:00Z", $"2026-01-0{i + 1}T00:00:00Z");
        }
        ISessionHistoryStore store = CreateStore();

        var result = await store.GetRecentSessionsAsync(maxCount: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetRecentSessionsAsync_TreatsEmptyRepository_AsNull()
    {
        InsertSession("session-1", "C:\\repo", repository: "", summary: "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z");
        ISessionHistoryStore store = CreateStore();

        var result = await store.GetRecentSessionsAsync(maxCount: 10);

        Assert.Null(result[0].Repository);
        Assert.Null(result[0].Summary);
    }

    [Fact]
    public async Task GetSessionAsync_ReturnsNull_WhenIdNotFound()
    {
        ISessionHistoryStore store = CreateStore();

        Assert.Null(await store.GetSessionAsync("does-not-exist"));
    }

    [Fact]
    public async Task GetSessionAsync_ReturnsMatchingSession()
    {
        InsertSession("session-1", "C:\\repo", "acme/widget", "Fixed the thing", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");
        ISessionHistoryStore store = CreateStore();

        var result = await store.GetSessionAsync("session-1");

        Assert.NotNull(result);
        Assert.Equal("C:\\repo", result!.Cwd);
        Assert.Equal("acme/widget", result.Repository);
        Assert.Equal("Fixed the thing", result.Summary);
    }

    [Fact]
    public async Task GetTurnsAsync_ReturnsOrderedByTurnIndex()
    {
        InsertSession("session-1", null, null, null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO turns (session_id, turn_index, user_message, assistant_response, timestamp) VALUES " +
                "('session-1', 1, 'hello', 'hi there', '2026-01-01T00:01:00Z')," +
                "('session-1', 0, 'first', 'first reply', '2026-01-01T00:00:00Z')";
            command.ExecuteNonQuery();
        }
        ISessionHistoryStore store = CreateStore();

        var turns = await store.GetTurnsAsync("session-1");

        Assert.Equal(2, turns.Count);
        Assert.Equal(0, turns[0].TurnIndex);
        Assert.Equal("first", turns[0].UserMessage);
        Assert.Equal(1, turns[1].TurnIndex);
    }

    [Fact]
    public async Task GetCheckpointsAsync_ReturnsOrderedByCheckpointNumber()
    {
        InsertSession("session-1", null, null, null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO checkpoints (session_id, checkpoint_number, title, overview, created_at) VALUES " +
                "('session-1', 2, 'Second', 'overview 2', '2026-01-01T00:02:00Z')," +
                "('session-1', 1, 'First', 'overview 1', '2026-01-01T00:01:00Z')";
            command.ExecuteNonQuery();
        }
        ISessionHistoryStore store = CreateStore();

        var checkpoints = await store.GetCheckpointsAsync("session-1");

        Assert.Equal(2, checkpoints.Count);
        Assert.Equal(1, checkpoints[0].CheckpointNumber);
        Assert.Equal("First", checkpoints[0].Title);
        Assert.Equal(2, checkpoints[1].CheckpointNumber);
    }
}
