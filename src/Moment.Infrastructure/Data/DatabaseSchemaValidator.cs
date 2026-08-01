using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Moment.Infrastructure.Data;

internal static class DatabaseSchemaValidator
{
    internal const string CreateTodosTableSql = """
        CREATE TABLE IF NOT EXISTS todos (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 200),
            created_at TEXT NOT NULL,
            due_date TEXT NULL CHECK(
                due_date IS NULL OR (
                    length(due_date) = 10 AND
                    due_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
                )
            ),
            importance INTEGER NOT NULL CHECK(importance IN (0, 1)),
            is_completed INTEGER NOT NULL CHECK(is_completed IN (0, 1)),
            completed_at TEXT NULL,
            CHECK(
                (is_completed = 0 AND completed_at IS NULL) OR
                (is_completed = 1 AND completed_at IS NOT NULL)
            )
        );
        """;

    private static readonly ColumnShape[] TodoColumns =
    [
        new("id", "TEXT", false, true),
        new("title", "TEXT", true, false),
        new("created_at", "TEXT", true, false),
        new("due_date", "TEXT", false, false),
        new("importance", "INTEGER", true, false),
        new("is_completed", "INTEGER", true, false),
        new("completed_at", "TEXT", false, false)
    ];

    internal static async Task<int> CountVersionMarkersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM schema_info WHERE version = $version;";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    internal static async Task<bool> TodosTableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'todos';
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture) == 1;
    }

    internal static async Task ValidateVersionThreeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        var markerCount = await CountVersionMarkersAsync(
            connection, transaction, 3, ct);
        if (markerCount != 1)
        {
            throw new InvalidDataException(
                $"Schema version 3 must have exactly one marker; found {markerCount}.");
        }

        await ValidateTodosTableAsync(connection, transaction, ct);
    }

    internal static async Task ValidateTodosTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        var createSql = await ReadCreateTableSqlAsync(
            connection, transaction, ct);
        var columns = await ReadTodoColumnsAsync(
            connection, transaction, ct);
        if (!columns.SequenceEqual(TodoColumns))
            throw new InvalidDataException("The todos table has an invalid column shape.");

        if (!HasCanonicalCreateBody(createSql))
        {
            throw new InvalidDataException(
                "The todos table is missing required constraints.");
        }

        await ValidateTodoPrimaryKeyIndexAsync(connection, transaction, ct);
    }

    private static async Task<string> ReadCreateTableSqlAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'todos';
            """;
        var value = await command.ExecuteScalarAsync(ct);
        if (value is not string sql || string.IsNullOrWhiteSpace(sql))
            throw new InvalidDataException("Schema version 3 requires a todos table.");
        return sql;
    }

    private static async Task<IReadOnlyList<ColumnShape>> ReadTodoColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(todos);";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<ColumnShape>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(4))
            {
                throw new InvalidDataException(
                    "The todos table must not define column defaults.");
            }
            columns.Add(new ColumnShape(
                reader.GetString(1),
                reader.GetString(2).ToUpperInvariant(),
                reader.GetInt32(3) == 1,
                reader.GetInt32(5) == 1));
        }
        return columns;
    }

    private static async Task ValidateTodoPrimaryKeyIndexAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        var primaryIndexes = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA index_list(todos);";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetInt32(2) == 1 &&
                    string.Equals(reader.GetString(3), "pk", StringComparison.Ordinal) &&
                    reader.GetInt32(4) == 0)
                {
                    primaryIndexes.Add(reader.GetString(1));
                }
            }
        }

        if (primaryIndexes.Count != 1)
            throw new InvalidDataException("The todos table has an invalid primary-key index.");

        await using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText =
            $"PRAGMA index_info(\"{primaryIndexes[0].Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        await using var indexReader = await indexCommand.ExecuteReaderAsync(ct);
        if (!await indexReader.ReadAsync(ct) ||
            indexReader.GetInt32(0) != 0 ||
            indexReader.GetInt32(1) != 0 ||
            !string.Equals(indexReader.GetString(2), "id", StringComparison.Ordinal) ||
            await indexReader.ReadAsync(ct))
        {
            throw new InvalidDataException("The todos primary-key index must contain only id.");
        }
    }

    private static bool HasCanonicalCreateBody(string actualSql) =>
        GetCreateBody(NormalizeSql(actualSql)) ==
        GetCreateBody(NormalizeSql(CreateTodosTableSql));

    private static string NormalizeSql(string sql) =>
        new(sql.Where(static character =>
                !char.IsWhiteSpace(character) && character != ';')
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string GetCreateBody(string normalizedSql)
    {
        const string canonicalPrefix = "createtabletodos(";
        const string idempotentPrefix = "createtableifnotexiststodos(";
        if (normalizedSql.StartsWith(idempotentPrefix, StringComparison.Ordinal))
            return normalizedSql[idempotentPrefix.Length..];
        if (normalizedSql.StartsWith(canonicalPrefix, StringComparison.Ordinal))
            return normalizedSql[canonicalPrefix.Length..];
        return normalizedSql;
    }

    private sealed record ColumnShape(
        string Name,
        string Type,
        bool NotNull,
        bool PrimaryKey);
}
