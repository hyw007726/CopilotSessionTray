using Microsoft.Data.Sqlite;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="ISessionHistoryStore"/> — read-only
/// access to <c>%USERPROFILE%\.copilot\session-store.db</c>. Schema
/// verified against a real, populated database while implementing this —
/// see IMPLEMENTATION_PLAN.md §2.2.
/// </summary>
public sealed class SessionHistoryStore : ISessionHistoryStore
{
    private static readonly string DefaultDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-store.db");

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>
    /// Creates a store against the real <c>session-store.db</c>. An explicit
    /// <paramref name="databasePath"/> override exists solely so xUnit tests can point this at a
    /// temp fixture database instead of the real <c>.copilot</c> folder — every real call site
    /// uses the parameterless default.
    /// </summary>
    public SessionHistoryStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? DefaultDatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public async Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, cwd, repository, host_type, summary, created_at, updated_at " +
                "FROM sessions ORDER BY updated_at DESC LIMIT $maxCount";
            command.Parameters.AddWithValue("$maxCount", maxCount);

            var results = new List<SessionSummary>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadSessionSummary(reader));
            }

            return (IReadOnlyList<SessionSummary>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, cwd, repository, host_type, summary, created_at, updated_at " +
                "FROM sessions WHERE id = $id";
            command.Parameters.AddWithValue("$id", sessionId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSessionSummary(reader) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT session_id, turn_index, user_message, assistant_response, timestamp " +
                "FROM turns WHERE session_id = $id ORDER BY turn_index";
            command.Parameters.AddWithValue("$id", sessionId);

            var results = new List<SessionTurn>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new SessionTurn(
                    SessionId: reader.GetString(0),
                    TurnIndex: reader.GetInt32(1),
                    UserMessage: reader.IsDBNull(2) ? null : reader.GetString(2),
                    AssistantResponse: reader.IsDBNull(3) ? null : reader.GetString(3),
                    TimestampUtc: DateTimeOffset.Parse(reader.GetString(4))));
            }

            return (IReadOnlyList<SessionTurn>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionCheckpoint>> GetCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT session_id, checkpoint_number, title, overview, created_at " +
                "FROM checkpoints WHERE session_id = $id ORDER BY checkpoint_number";
            command.Parameters.AddWithValue("$id", sessionId);

            var results = new List<SessionCheckpoint>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new SessionCheckpoint(
                    SessionId: reader.GetString(0),
                    CheckpointNumber: reader.GetInt32(1),
                    Title: reader.IsDBNull(2) ? null : reader.GetString(2),
                    Overview: reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(4))));
            }

            return (IReadOnlyList<SessionCheckpoint>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SessionSummary ReadSessionSummary(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Cwd: reader.IsDBNull(1) ? null : reader.GetString(1),
        Repository: reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? null : reader.GetString(2),
        HostType: reader.IsDBNull(3) ? null : reader.GetString(3),
        Summary: reader.IsDBNull(4) || reader.GetString(4).Length == 0 ? null : reader.GetString(4),
        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(5)),
        UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(6)));

    /// <summary>
    /// Opens a fresh read-only connection and runs <paramref name="query"/>, retrying briefly on
    /// SQLITE_BUSY/SQLITE_LOCKED — <c>session-store.db</c> is WAL-mode and actively written by
    /// live Copilot CLI processes, so a momentary lock conflict is expected, not exceptional, per
    /// this interface's documented contract.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(Func<SqliteConnection, Task<T>> query, CancellationToken cancellationToken)
    {
        const int busyErrorCode = 5;
        const int lockedErrorCode = 6;

        SqliteException? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return await query(connection).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is busyErrorCode or lockedErrorCode)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"'{_databasePath}' stayed busy/locked after multiple retries.", lastError);
    }
}
