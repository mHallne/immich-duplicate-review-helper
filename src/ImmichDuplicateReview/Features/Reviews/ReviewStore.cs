using System.Globalization;
using System.Text.Json;
using ImmichDuplicateReview.Features.Batches;
using Microsoft.Data.Sqlite;

namespace ImmichDuplicateReview.Features.Reviews;

public sealed record ReviewSession(long Id, int BatchSize, string SortMode, int CurrentPosition);
public sealed record ReviewProgress(int Total, int Reviewed, int Skipped, int Failed, int Remaining);

public sealed class ReviewStore(string databasePath) : IAsyncDisposable
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS duplicate_group (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                immich_group_id TEXT NOT NULL UNIQUE,
                sort_date TEXT NOT NULL,
                asset_count INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('pending','reviewed','skipped','failed')),
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                asset_metadata_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS review (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL UNIQUE REFERENCES duplicate_group(id),
                status TEXT NOT NULL CHECK(status IN ('reviewed','skipped','failed')),
                decision_json TEXT,
                reviewed_at TEXT NOT NULL,
                notes TEXT
            );
            CREATE TABLE IF NOT EXISTS review_session (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                batch_size INTEGER NOT NULL,
                sort_mode TEXT NOT NULL,
                current_position INTEGER NOT NULL DEFAULT 0,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS review_action (
                group_id INTEGER PRIMARY KEY REFERENCES duplicate_group(id),
                resolve_completed_at TEXT,
                stack_completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS review_session_group (
                session_id INTEGER NOT NULL REFERENCES review_session(id) ON DELETE CASCADE,
                group_id INTEGER NOT NULL REFERENCES duplicate_group(id),
                position INTEGER NOT NULL,
                PRIMARY KEY (session_id, group_id),
                UNIQUE (session_id, position)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task UpsertGroupsAsync(IEnumerable<DuplicateGroup> groups, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var group in groups)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO duplicate_group
                    (immich_group_id, sort_date, asset_count, status, first_seen_at, last_seen_at, asset_metadata_json)
                VALUES ($id, $sortDate, $count, 'pending', $now, $now, $assets)
                ON CONFLICT(immich_group_id) DO UPDATE SET
                    sort_date = excluded.sort_date,
                    asset_count = excluded.asset_count,
                    last_seen_at = excluded.last_seen_at,
                    asset_metadata_json = excluded.asset_metadata_json;
                """;
            command.Parameters.AddWithValue("$id", group.Id);
            command.Parameters.AddWithValue("$sortDate", group.SortDate.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$count", group.Assets.Count);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$assets", JsonSerializer.Serialize(group.Assets));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SkipAsync(string groupId, string? notes, CancellationToken cancellationToken = default) =>
        RecordAsync(groupId, ReviewStatus.Skipped, null, notes, cancellationToken);

    public Task MarkReviewedAsync(string groupId, string decisionJson, CancellationToken cancellationToken = default) =>
        RecordAsync(groupId, ReviewStatus.Reviewed, decisionJson, null, cancellationToken);

    public Task MarkFailedAsync(string groupId, string decisionJson, string notes, CancellationToken cancellationToken = default) =>
        RecordAsync(groupId, ReviewStatus.Failed, decisionJson, notes, cancellationToken);

    public Task<bool> IsResolveCompletedAsync(string groupId, CancellationToken cancellationToken = default) =>
        IsActionCompletedAsync(groupId, "resolve_completed_at", cancellationToken);

    public Task MarkResolveCompletedAsync(string groupId, CancellationToken cancellationToken = default) =>
        MarkActionCompletedAsync(groupId, "resolve_completed_at", cancellationToken);

    public Task MarkStackCompletedAsync(string groupId, CancellationToken cancellationToken = default) =>
        MarkActionCompletedAsync(groupId, "stack_completed_at", cancellationToken);

    public async Task<ReviewStatus?> GetStatusAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM duplicate_group WHERE immich_group_id = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : Enum.Parse<ReviewStatus>(result, true);
    }

    public async Task<IReadOnlyList<DuplicateGroup>> LoadPendingAsync(CancellationToken cancellationToken = default)
    {
        var groups = new List<DuplicateGroup>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT immich_group_id, asset_metadata_json
            FROM duplicate_group
            WHERE status IN ('pending', 'failed')
            ORDER BY sort_date, immich_group_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var assets = JsonSerializer.Deserialize<DuplicateAsset[]>(reader.GetString(1))
                ?? throw new InvalidDataException("Stored asset metadata is invalid.");
            groups.Add(new DuplicateGroup(reader.GetString(0), assets));
        }
        return groups;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> LoadActiveBatchAsync(CancellationToken cancellationToken = default)
    {
        var groups = new List<DuplicateGroup>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dg.immich_group_id, dg.asset_metadata_json, dg.status
            FROM review_session rs
            JOIN review_session_group rsg ON rsg.session_id = rs.id
            JOIN duplicate_group dg ON dg.id = rsg.group_id
            WHERE rs.completed_at IS NULL
            ORDER BY rsg.position;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) groups.Add(ReadGroup(reader));
        return groups;
    }

    public async Task<DuplicateGroup?> LoadNextActiveGroupAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dg.immich_group_id, dg.asset_metadata_json, dg.status
            FROM review_session rs
            JOIN review_session_group rsg ON rsg.session_id = rs.id
            JOIN duplicate_group dg ON dg.id = rsg.group_id
            WHERE rs.completed_at IS NULL AND dg.status IN ('pending', 'failed')
            ORDER BY rsg.position LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGroup(reader) : null;
    }

    public async Task<ReviewProgress> GetActiveProgressAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(dg.status = 'reviewed'), 0),
                   COALESCE(SUM(dg.status = 'skipped'), 0),
                   COALESCE(SUM(dg.status = 'failed'), 0),
                   COALESCE(SUM(dg.status IN ('pending', 'failed')), 0)
            FROM review_session rs
            JOIN review_session_group rsg ON rsg.session_id = rs.id
            JOIN duplicate_group dg ON dg.id = rsg.group_id
            WHERE rs.completed_at IS NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, 0, 0, 0, 0);
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    public async Task<DuplicateGroup?> LoadGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT asset_metadata_json, status FROM duplicate_group WHERE immich_group_id = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var assets = JsonSerializer.Deserialize<DuplicateAsset[]>(reader.GetString(0))
            ?? throw new InvalidDataException("Stored asset metadata is invalid.");
        return new DuplicateGroup(groupId, assets, Enum.Parse<ReviewStatus>(reader.GetString(1), true));
    }

    public async Task<ReviewSession> CreateOrResumeSessionAsync(int batchSize, CancellationToken cancellationToken = default)
        => await CreateOrResumeSessionCoreAsync(batchSize, [], cancellationToken);

    public async Task<ReviewSession> CreateOrResumeSessionAsync(int batchSize, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
        => await CreateOrResumeSessionCoreAsync(batchSize, groupIds, cancellationToken);

    private async Task<ReviewSession> CreateOrResumeSessionCoreAsync(int batchSize, IReadOnlyList<string> groupIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var find = connection.CreateCommand())
        {
            find.CommandText = """
                SELECT id, batch_size, sort_mode, current_position
                FROM review_session WHERE completed_at IS NULL ORDER BY id DESC LIMIT 1;
                """;
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existing = new ReviewSession(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3));
                await reader.DisposeAsync();
                await PopulateSessionAsync(connection, existing.Id, groupIds, cancellationToken);
                return existing;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO review_session (created_at, batch_size, sort_mode, current_position)
            VALUES ($created, $size, 'oldest', 0);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$size", batchSize);
        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Session was not created."));
        await PopulateSessionAsync(connection, id, groupIds, cancellationToken);
        return new(id, batchSize, "oldest", 0);
    }

    private async Task RecordAsync(string groupId, ReviewStatus status, string? decisionJson, string? notes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO review (group_id, status, decision_json, reviewed_at, notes)
            SELECT id, $status, $decision, $reviewed, $notes FROM duplicate_group WHERE immich_group_id = $id
            ON CONFLICT(group_id) DO UPDATE SET
                status = excluded.status, decision_json = excluded.decision_json,
                reviewed_at = excluded.reviewed_at, notes = excluded.notes;
            UPDATE duplicate_group SET status = $status WHERE immich_group_id = $id;
            UPDATE review_session SET current_position = current_position + 1
            WHERE completed_at IS NULL AND $status <> 'failed';
            UPDATE review_session SET completed_at = $reviewed
            WHERE completed_at IS NULL
              AND EXISTS (SELECT 1 FROM review_session_group WHERE session_id = review_session.id)
              AND NOT EXISTS (
                SELECT 1 FROM review_session_group rsg
                JOIN duplicate_group dg ON dg.id = rsg.group_id
                WHERE rsg.session_id = review_session.id AND dg.status IN ('pending', 'failed')
              );
            """;
        command.Parameters.AddWithValue("$id", groupId);
        command.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$decision", (object?)decisionJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException($"Group '{groupId}' was not found.");
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<bool> IsActionCompletedAsync(string groupId, string column, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ra.{column} IS NOT NULL
            FROM duplicate_group dg LEFT JOIN review_action ra ON ra.group_id = dg.id
            WHERE dg.immich_group_id = $id;
            """;
        command.Parameters.AddWithValue("$id", groupId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private async Task MarkActionCompletedAsync(string groupId, string column, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO review_action (group_id, {column})
            SELECT id, $completed FROM duplicate_group WHERE immich_group_id = $id
            ON CONFLICT(group_id) DO UPDATE SET {column} = excluded.{column};
            """;
        command.Parameters.AddWithValue("$id", groupId);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException($"Group '{groupId}' was not found.");
    }

    private static async Task<int> CountCompletedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM duplicate_group WHERE status <> 'pending';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task PopulateSessionAsync(SqliteConnection connection, long sessionId, IReadOnlyList<string> groupIds, CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0) return;
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM review_session_group WHERE session_id = $sessionId;";
        count.Parameters.AddWithValue("$sessionId", sessionId);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0) return;

        for (var position = 0; position < groupIds.Count; position++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO review_session_group (session_id, group_id, position)
                SELECT $sessionId, id, $position FROM duplicate_group WHERE immich_group_id = $groupId;
                """;
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$groupId", groupIds[position]);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new KeyNotFoundException($"Group '{groupIds[position]}' was not found.");
        }
    }

    private static DuplicateGroup ReadGroup(SqliteDataReader reader)
    {
        var assets = JsonSerializer.Deserialize<DuplicateAsset[]>(reader.GetString(1))
            ?? throw new InvalidDataException("Stored asset metadata is invalid.");
        return new DuplicateGroup(reader.GetString(0), assets, Enum.Parse<ReviewStatus>(reader.GetString(2), true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
