using Microsoft.Data.Sqlite;

namespace Moment.Infrastructure.Data;

public static class DatabaseMigrator
{
    public static async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        await foreignKeys.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);

        command.CommandText = "SELECT COUNT(*) FROM schema_info WHERE version = 1;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE items (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    importance INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE occurrences (
                    id TEXT PRIMARY KEY,
                    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                    due_at TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    handled_at TEXT NULL,
                    snooze_parent_id TEXT NULL,
                    UNIQUE(item_id, due_at)
                );
                CREATE TABLE recurrence_rules (
                    item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                    kind INTEGER NOT NULL,
                    days_of_week TEXT NOT NULL,
                    time TEXT NOT NULL
                );
                CREATE TABLE action_log (
                    id TEXT PRIMARY KEY,
                    occurrence_id TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
                    state INTEGER NOT NULL,
                    handled_at TEXT NOT NULL
                );
                CREATE TABLE settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE INDEX ix_occurrences_state_due_at ON occurrences(state, due_at);
                CREATE INDEX ix_occurrences_item_id ON occurrences(item_id);
                INSERT INTO schema_info(version) VALUES (1);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
