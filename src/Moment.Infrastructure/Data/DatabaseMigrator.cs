using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Moment.Infrastructure.Data;

public static class DatabaseMigrator
{
    public static Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken ct) =>
        OpenConnectionAsync(databasePath, ct, SqliteCacheMode.Shared);

    internal static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken ct,
        SqliteCacheMode cache)
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
            Cache = cache,
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

        var versionFourMarkers =
            await DatabaseSchemaValidator.CountVersionMarkersAsync(
                connection, (SqliteTransaction)transaction, 4, ct);
        if (versionFourMarkers > 0)
        {
            await DatabaseSchemaValidator.ValidateVersionFourAsync(
                connection, (SqliteTransaction)transaction, ct);
            await transaction.CommitAsync(ct);
            return;
        }

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
                    due_at_utc TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    handled_at TEXT NULL,
                    snooze_parent_id TEXT NULL,
                    UNIQUE(item_id, due_at_utc)
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
                CREATE INDEX ix_occurrences_state_due_at ON occurrences(state, due_at_utc);
                CREATE INDEX ix_occurrences_item_id ON occurrences(item_id);
                INSERT INTO schema_info(version) VALUES (1);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        command.CommandText = "SELECT COUNT(*) FROM schema_info WHERE version = 2;";
        var versionTwoExists = Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        if (!versionTwoExists)
        {
            if (!await HasColumnAsync(connection, (SqliteTransaction)transaction, "occurrences", "due_at_utc", ct))
            {
                command.CommandText = "ALTER TABLE occurrences ADD COLUMN due_at_utc TEXT NULL;";
                await command.ExecuteNonQueryAsync(ct);
            }

            await PopulateUtcKeysAsync(connection, (SqliteTransaction)transaction, ct);
            command.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_occurrences_item_due_at_utc
                    ON occurrences(item_id, due_at_utc);
                CREATE INDEX IF NOT EXISTS ix_occurrences_state_due_at_utc
                    ON occurrences(state, due_at_utc);
                INSERT INTO schema_info(version) VALUES (2);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        var versionThreeMarkers =
            await DatabaseSchemaValidator.CountVersionMarkersAsync(
                connection, (SqliteTransaction)transaction, 3, ct);
        if (versionThreeMarkers > 1)
        {
            throw new InvalidDataException(
                $"Schema version 3 must have exactly one marker; found {versionThreeMarkers}.");
        }
        if (versionThreeMarkers == 0)
        {
            if (await DatabaseSchemaValidator.TodosTableExistsAsync(
                    connection, (SqliteTransaction)transaction, ct))
            {
                await DatabaseSchemaValidator.ValidateTodosTableAsync(
                    connection, (SqliteTransaction)transaction, ct);
            }
            else
            {
                command.CommandText = DatabaseSchemaValidator.CreateTodosTableSql;
                await command.ExecuteNonQueryAsync(ct);
            }

            command.CommandText = "INSERT INTO schema_info(version) VALUES (3);";
            await command.ExecuteNonQueryAsync(ct);
        }

        await DatabaseSchemaValidator.ValidateVersionThreeUpgradeSourceAsync(
            connection, (SqliteTransaction)transaction, ct);
        if (await HasColumnAsync(
                connection, (SqliteTransaction)transaction,
                "occurrences", "deleted_at", ct) ||
            await HasColumnAsync(
                connection, (SqliteTransaction)transaction,
                "todos", "deleted_at", ct))
        {
            throw new InvalidDataException(
                "Schema version 4 columns exist without a version marker.");
        }

        await UpgradeToVersionFourAsync(command, ct);
        foreach (var indexSql in DatabaseSchemaValidator.CreateVersionFourIndexesSql)
        {
            command.CommandText = indexSql;
            await command.ExecuteNonQueryAsync(ct);
        }

        command.CommandText = "INSERT INTO schema_info(version) VALUES (4);";
        await command.ExecuteNonQueryAsync(ct);

        await DatabaseSchemaValidator.ValidateVersionFourAsync(
            connection, (SqliteTransaction)transaction, ct);

        await transaction.CommitAsync(ct);
    }

    private static async Task UpgradeToVersionFourAsync(
        SqliteCommand command,
        CancellationToken ct)
    {
        var occurrencesSql = DatabaseSchemaValidator.CreateOccurrencesVersionFourSql.Replace(
            "CREATE TABLE occurrences",
            "CREATE TABLE occurrences_v4",
            StringComparison.Ordinal);
        var actionLogSql = DatabaseSchemaValidator.CreateActionLogVersionFourSql
            .Replace(
                "CREATE TABLE action_log",
                "CREATE TABLE action_log_v4",
                StringComparison.Ordinal)
            .Replace(
                "REFERENCES occurrences(id)",
                "REFERENCES occurrences_v4(id)",
                StringComparison.Ordinal);

        command.CommandText = $"""
            {occurrencesSql}
            INSERT INTO occurrences_v4 (
                id, item_id, due_at, due_at_utc, state,
                handled_at, snooze_parent_id, deleted_at)
            SELECT
                id, item_id, due_at, due_at_utc, state,
                handled_at, snooze_parent_id, NULL
            FROM occurrences;

            {actionLogSql}
            INSERT INTO action_log_v4 (id, occurrence_id, state, handled_at)
            SELECT id, occurrence_id, state, handled_at
            FROM action_log;

            DROP TABLE action_log;
            DROP TABLE occurrences;
            ALTER TABLE occurrences_v4 RENAME TO occurrences;
            ALTER TABLE action_log_v4 RENAME TO action_log;
            ALTER TABLE todos ADD COLUMN deleted_at TEXT NULL;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task PopulateUtcKeysAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var rows = new List<(string Id, string DueAt)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, due_at FROM occurrences WHERE due_at_utc IS NULL;";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var row in rows)
        {
            var dueAt = DateTimeOffset.Parse(row.DueAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE occurrences SET due_at_utc = $dueAtUtc WHERE id = $id;";
            update.Parameters.AddWithValue("$dueAtUtc", dueAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
    }
}
