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

    internal const string CreateOccurrencesVersionFourSql = """
        CREATE TABLE occurrences (
            id TEXT PRIMARY KEY,
            item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            due_at TEXT NOT NULL,
            due_at_utc TEXT NOT NULL,
            state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5)),
            handled_at TEXT NULL,
            snooze_parent_id TEXT NULL,
            deleted_at TEXT NULL
        );
        """;

    internal const string CreateActionLogVersionFourSql = """
        CREATE TABLE action_log (
            id TEXT PRIMARY KEY,
            occurrence_id TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
            state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5)),
            handled_at TEXT NOT NULL
        );
        """;

    internal static readonly string[] CreateVersionFourIndexesSql =
    [
        "CREATE UNIQUE INDEX ux_occurrences_active_item_due_at_utc ON occurrences(item_id, due_at_utc) WHERE deleted_at IS NULL;",
        "CREATE INDEX ix_occurrences_active_state_due_at_utc ON occurrences(state, due_at_utc) WHERE deleted_at IS NULL;",
        "CREATE INDEX ix_occurrences_item_id ON occurrences(item_id);",
        "CREATE INDEX ix_occurrences_deleted_due_at_utc ON occurrences(deleted_at, due_at_utc);",
        "CREATE INDEX ix_occurrences_deleted_handled_at ON occurrences(deleted_at, handled_at);",
        "CREATE INDEX ix_todos_active_due_date ON todos(due_date, id) WHERE deleted_at IS NULL;",
        "CREATE INDEX ix_todos_deleted_due_date ON todos(deleted_at, due_date);",
        "CREATE INDEX ix_todos_deleted_completed_at ON todos(deleted_at, completed_at);",
        "CREATE INDEX ix_occurrences_due_at_utc_id ON occurrences(due_at_utc, id);",
        "CREATE INDEX ix_occurrences_handled_at_id ON occurrences(handled_at, id);",
        "CREATE INDEX ix_todos_due_date_id ON todos(due_date, id);",
        "CREATE INDEX ix_todos_completed_at_id ON todos(completed_at, id);",
        "CREATE INDEX ix_action_log_handled_at_occurrence_id ON action_log(handled_at, occurrence_id, id);"
    ];

    internal static IReadOnlyList<string> CreateVersionFourAnalyticsIndexesSql =>
        CreateVersionFourIndexesSql.AsSpan(8).ToArray();

    private const string CreateSchemaInfoSql =
        "CREATE TABLE schema_info (version INTEGER NOT NULL);";

    private const string CreateItemsSql = """
        CREATE TABLE items (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            kind INTEGER NOT NULL,
            importance INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );
        """;

    private const string CreateRecurrenceRulesSql = """
        CREATE TABLE recurrence_rules (
            item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            days_of_week TEXT NOT NULL,
            time TEXT NOT NULL
        );
        """;

    private const string CreateActionLogVersionThreeSql = """
        CREATE TABLE action_log (
            id TEXT PRIMARY KEY,
            occurrence_id TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
            state INTEGER NOT NULL,
            handled_at TEXT NOT NULL
        );
        """;

    private const string CreateSettingsSql = """
        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private static readonly ColumnShape[] SchemaInfoColumns =
    [
        new("version", "INTEGER", true, false)
    ];

    private static readonly ColumnShape[] ItemColumns =
    [
        new("id", "TEXT", false, true),
        new("title", "TEXT", true, false),
        new("kind", "INTEGER", true, false),
        new("importance", "INTEGER", true, false),
        new("created_at", "TEXT", true, false)
    ];

    private static readonly ColumnShape[] OccurrenceVersionThreeColumns =
    [
        new("id", "TEXT", false, true),
        new("item_id", "TEXT", true, false),
        new("due_at", "TEXT", true, false),
        new("due_at_utc", "TEXT", true, false),
        new("state", "INTEGER", true, false),
        new("handled_at", "TEXT", false, false),
        new("snooze_parent_id", "TEXT", false, false)
    ];

    private static readonly ColumnShape[] OccurrenceMigratedVersionOneColumns =
    [
        new("id", "TEXT", false, true),
        new("item_id", "TEXT", true, false),
        new("due_at", "TEXT", true, false),
        new("state", "INTEGER", true, false),
        new("handled_at", "TEXT", false, false),
        new("snooze_parent_id", "TEXT", false, false),
        new("due_at_utc", "TEXT", false, false)
    ];

    private static readonly ColumnShape[] OccurrenceVersionFourColumns =
    [
        .. OccurrenceVersionThreeColumns,
        new("deleted_at", "TEXT", false, false)
    ];

    private static readonly ColumnShape[] RecurrenceColumns =
    [
        new("item_id", "TEXT", false, true),
        new("kind", "INTEGER", true, false),
        new("days_of_week", "TEXT", true, false),
        new("time", "TEXT", true, false)
    ];

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

    private static readonly ColumnShape[] TodoColumnsVersionFour =
    [
        .. TodoColumns,
        new("deleted_at", "TEXT", false, false)
    ];

    private static readonly ColumnShape[] ActionLogColumns =
    [
        new("id", "TEXT", false, true),
        new("occurrence_id", "TEXT", true, false),
        new("state", "INTEGER", true, false),
        new("handled_at", "TEXT", true, false)
    ];

    private static readonly ColumnShape[] SettingsColumns =
    [
        new("key", "TEXT", false, true),
        new("value", "TEXT", true, false)
    ];

    private static readonly ForeignKeyShape[] ItemForeignKey =
    [
        new("items", "item_id", "id", "NO ACTION", "CASCADE", "NONE")
    ];

    private static readonly ForeignKeyShape[] OccurrenceForeignKey =
    [
        new("occurrences", "occurrence_id", "id", "NO ACTION", "CASCADE", "NONE")
    ];

    private static readonly IndexShape[] VersionFourIndexes =
    [
        new("occurrences", "ux_occurrences_active_item_due_at_utc", true, true,
            ["item_id", "due_at_utc"], CreateVersionFourIndexesSql[0]),
        new("occurrences", "ix_occurrences_active_state_due_at_utc", false, true,
            ["state", "due_at_utc"], CreateVersionFourIndexesSql[1]),
        new("occurrences", "ix_occurrences_item_id", false, false,
            ["item_id"], CreateVersionFourIndexesSql[2]),
        new("occurrences", "ix_occurrences_deleted_due_at_utc", false, false,
            ["deleted_at", "due_at_utc"], CreateVersionFourIndexesSql[3]),
        new("occurrences", "ix_occurrences_deleted_handled_at", false, false,
            ["deleted_at", "handled_at"], CreateVersionFourIndexesSql[4]),
        new("todos", "ix_todos_active_due_date", false, true,
            ["due_date", "id"], CreateVersionFourIndexesSql[5]),
        new("todos", "ix_todos_deleted_due_date", false, false,
            ["deleted_at", "due_date"], CreateVersionFourIndexesSql[6]),
        new("todos", "ix_todos_deleted_completed_at", false, false,
            ["deleted_at", "completed_at"], CreateVersionFourIndexesSql[7]),
        new("occurrences", "ix_occurrences_due_at_utc_id", false, false,
            ["due_at_utc", "id"], CreateVersionFourIndexesSql[8]),
        new("occurrences", "ix_occurrences_handled_at_id", false, false,
            ["handled_at", "id"], CreateVersionFourIndexesSql[9]),
        new("todos", "ix_todos_due_date_id", false, false,
            ["due_date", "id"], CreateVersionFourIndexesSql[10]),
        new("todos", "ix_todos_completed_at_id", false, false,
            ["completed_at", "id"], CreateVersionFourIndexesSql[11]),
        new("action_log", "ix_action_log_handled_at_occurrence_id", false, false,
            ["handled_at", "occurrence_id", "id"], CreateVersionFourIndexesSql[12])
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
        CancellationToken ct) =>
        await TableExistsAsync(connection, transaction, "todos", ct);

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

    internal static async Task ValidateVersionThreeUpgradeSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await ValidateExactMarkersAsync(
            connection, transaction, [1, 2, 3], ct);
        await ValidateTableAsync(
            connection, transaction, "schema_info", SchemaInfoColumns,
            CreateSchemaInfoSql, [], ct);
        await ValidateTableAsync(
            connection, transaction, "items", ItemColumns,
            CreateItemsSql, [], ct);

        var occurrenceColumns = await ReadColumnsAsync(
            connection, transaction, "occurrences", ct);
        if (!occurrenceColumns.SequenceEqual(OccurrenceVersionThreeColumns) &&
            !occurrenceColumns.SequenceEqual(OccurrenceMigratedVersionOneColumns))
        {
            throw new InvalidDataException(
                "The schema version 3 occurrences table has an invalid column shape.");
        }
        await ValidatePrimaryKeyIndexAsync(
            connection, transaction, "occurrences", "id", ct);
        await ValidateForeignKeysAsync(
            connection, transaction, "occurrences", ItemForeignKey, ct);

        await ValidateTableAsync(
            connection, transaction, "recurrence_rules", RecurrenceColumns,
            CreateRecurrenceRulesSql, ItemForeignKey, ct);
        await ValidateTodosTableAsync(connection, transaction, ct);
        await ValidateTableAsync(
            connection, transaction, "action_log", ActionLogColumns,
            CreateActionLogVersionThreeSql, OccurrenceForeignKey, ct);
        await ValidateTableAsync(
            connection, transaction, "settings", SettingsColumns,
            CreateSettingsSql, [], ct);
        await ValidateForeignKeyCheckAsync(connection, transaction, ct);
    }

    internal static async Task ValidateTodosTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct) =>
        await ValidateTableAsync(
            connection, transaction, "todos", TodoColumns,
            CreateTodosTableSql, [], ct);

    internal static async Task ValidateVersionFourAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct) =>
        await ValidateVersionFourAsync(
            connection, transaction, VersionFourIndexes.Length, ct);

    internal static async Task ValidateVersionFourUpgradeBaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct) =>
        await ValidateVersionFourAsync(connection, transaction, 8, ct);

    private static async Task ValidateVersionFourAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int requiredIndexCount,
        CancellationToken ct)
    {
        await ValidateExactMarkersAsync(
            connection, transaction, [1, 2, 3, 4], ct);
        await ValidateTableAsync(
            connection, transaction, "schema_info", SchemaInfoColumns,
            CreateSchemaInfoSql, [], ct);
        await ValidateTableAsync(
            connection, transaction, "items", ItemColumns,
            CreateItemsSql, [], ct);
        await ValidateTableAsync(
            connection, transaction, "occurrences", OccurrenceVersionFourColumns,
            CreateOccurrencesVersionFourSql, ItemForeignKey, ct);
        await ValidateTableAsync(
            connection, transaction, "recurrence_rules", RecurrenceColumns,
            CreateRecurrenceRulesSql, ItemForeignKey, ct);

        var versionFourTodosSql = CreateTodosTableSql.Replace(
            "    completed_at TEXT NULL,\n    CHECK(",
            "    completed_at TEXT NULL, deleted_at TEXT NULL,\n    CHECK(",
            StringComparison.Ordinal);
        await ValidateTableAsync(
            connection, transaction, "todos", TodoColumnsVersionFour,
            versionFourTodosSql, [], ct);
        await ValidateTableAsync(
            connection, transaction, "action_log", ActionLogColumns,
            CreateActionLogVersionFourSql, OccurrenceForeignKey, ct);
        await ValidateTableAsync(
            connection, transaction, "settings", SettingsColumns,
            CreateSettingsSql, [], ct);

        foreach (var index in VersionFourIndexes.Take(requiredIndexCount))
            await ValidateIndexAsync(connection, transaction, index, ct);
        await ValidateForeignKeyCheckAsync(connection, transaction, ct);
    }

    private static async Task ValidateTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ColumnShape> expectedColumns,
        string expectedSql,
        IReadOnlyList<ForeignKeyShape> expectedForeignKeys,
        CancellationToken ct)
    {
        var createSql = await ReadCreateTableSqlAsync(
            connection, transaction, table, ct);
        var columns = await ReadColumnsAsync(
            connection, transaction, table, ct);
        if (!columns.SequenceEqual(expectedColumns))
        {
            throw new InvalidDataException(
                $"The {table} table has an invalid column shape.");
        }
        if (!HasCanonicalCreateBody(createSql, expectedSql))
        {
            throw new InvalidDataException(
                $"The {table} table is missing required constraints.");
        }

        var primaryKey = expectedColumns.SingleOrDefault(
            static column => column.PrimaryKey);
        if (primaryKey is not null)
        {
            await ValidatePrimaryKeyIndexAsync(
                connection, transaction, table, primaryKey.Name, ct);
        }
        await ValidateForeignKeysAsync(
            connection, transaction, table, expectedForeignKeys, ct);
    }

    private static async Task ValidateExactMarkersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<int> expected,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_info ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var actual = new List<int>();
        while (await reader.ReadAsync(ct))
            actual.Add(reader.GetInt32(0));
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"Schema version markers are invalid: {string.Join(',', actual)}.");
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> ReadCreateTableSqlAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = $name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$name", table);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is not string sql || string.IsNullOrWhiteSpace(sql))
            throw new InvalidDataException($"Schema requires a {table} table.");
        return sql;
    }

    private static async Task<IReadOnlyList<ColumnShape>> ReadColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<ColumnShape>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(4))
            {
                throw new InvalidDataException(
                    $"The {table} table must not define column defaults.");
            }
            columns.Add(new ColumnShape(
                reader.GetString(1).ToLowerInvariant(),
                reader.GetString(2).ToUpperInvariant(),
                reader.GetInt32(3) == 1,
                reader.GetInt32(5) == 1));
        }
        return columns;
    }

    private static async Task ValidatePrimaryKeyIndexAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string column,
        CancellationToken ct)
    {
        var primaryIndexes = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)});";
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
            throw new InvalidDataException($"The {table} table has an invalid primary-key index.");

        await using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText =
            $"PRAGMA index_info({QuoteIdentifier(primaryIndexes[0])});";
        await using var indexReader = await indexCommand.ExecuteReaderAsync(ct);
        if (!await indexReader.ReadAsync(ct) ||
            indexReader.GetInt32(0) != 0 ||
            indexReader.GetInt32(1) != 0 ||
            !string.Equals(indexReader.GetString(2), column,
                StringComparison.OrdinalIgnoreCase) ||
            await indexReader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                $"The {table} primary-key index must contain only {column}.");
        }
    }

    private static async Task ValidateForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ForeignKeyShape> expected,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var actual = new List<ForeignKeyShape>();
        while (await reader.ReadAsync(ct))
        {
            actual.Add(new ForeignKeyShape(
                reader.GetString(2).ToLowerInvariant(),
                reader.GetString(3).ToLowerInvariant(),
                reader.GetString(4).ToLowerInvariant(),
                reader.GetString(5).ToUpperInvariant(),
                reader.GetString(6).ToUpperInvariant(),
                reader.GetString(7).ToUpperInvariant()));
        }
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"The {table} table has invalid foreign keys.");
        }
    }

    private static async Task ValidateIndexAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IndexShape expected,
        CancellationToken ct)
    {
        var matches = new List<(bool Unique, bool Partial, string Origin)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"PRAGMA index_list({QuoteIdentifier(expected.Table)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(
                        reader.GetString(1), expected.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((
                        reader.GetInt32(2) == 1,
                        reader.GetInt32(4) == 1,
                        reader.GetString(3)));
                }
            }
        }
        if (matches.Count != 1 ||
            matches[0].Unique != expected.Unique ||
            matches[0].Partial != expected.Partial ||
            !string.Equals(matches[0].Origin, "c", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Required index {expected.Name} has invalid flags.");
        }

        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.Transaction = transaction;
            columnsCommand.CommandText =
                $"PRAGMA index_info({QuoteIdentifier(expected.Name)});";
            await using var reader = await columnsCommand.ExecuteReaderAsync(ct);
            var columns = new List<string>();
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(2).ToLowerInvariant());
            if (!columns.SequenceEqual(expected.Columns))
            {
                throw new InvalidDataException(
                    $"Required index {expected.Name} has invalid columns.");
            }
        }

        await using var sqlCommand = connection.CreateCommand();
        sqlCommand.Transaction = transaction;
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index' AND name = $name COLLATE NOCASE;
            """;
        sqlCommand.Parameters.AddWithValue("$name", expected.Name);
        var sql = await sqlCommand.ExecuteScalarAsync(ct) as string;
        if (sql is null ||
            !TokenizeSql(sql).SequenceEqual(TokenizeSql(expected.CreateSql)))
        {
            throw new InvalidDataException(
                $"Required index {expected.Name} has an invalid definition.");
        }
    }

    private static async Task ValidateForeignKeyCheckAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            throw new InvalidDataException("Schema contains foreign-key violations.");
    }

    private static bool HasCanonicalCreateBody(
        string actualSql,
        string expectedSql) =>
        TryGetCreateBody(TokenizeSql(actualSql), out var actualBody) &&
        TryGetCreateBody(TokenizeSql(expectedSql), out var expectedBody) &&
        actualBody.SequenceEqual(expectedBody);

    private static IReadOnlyList<SqlToken> TokenizeSql(string sql)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character is '\'' or '"' or '`' or '[')
            {
                var (token, nextIndex) = ReadQuotedToken(sql, index);
                tokens.Add(token);
                index = nextIndex;
                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                var start = index++;
                while (index < sql.Length &&
                       (char.IsLetterOrDigit(sql[index]) ||
                        sql[index] is '_' or '$'))
                {
                    index++;
                }
                tokens.Add(new SqlToken(
                    SqlTokenKind.Word,
                    sql[start..index].ToLowerInvariant()));
                continue;
            }

            if (char.IsDigit(character))
            {
                var start = index++;
                while (index < sql.Length && char.IsDigit(sql[index]))
                    index++;
                tokens.Add(new SqlToken(
                    SqlTokenKind.Number,
                    sql[start..index]));
                continue;
            }

            if (IsOperatorCharacter(character))
            {
                var start = index++;
                while (index < sql.Length && IsOperatorCharacter(sql[index]))
                    index++;
                tokens.Add(new SqlToken(
                    SqlTokenKind.Operator,
                    sql[start..index]));
                continue;
            }

            tokens.Add(new SqlToken(
                SqlTokenKind.Punctuation,
                character.ToString()));
            index++;
        }

        if (tokens.Count > 0 &&
            tokens[^1] == new SqlToken(SqlTokenKind.Punctuation, ";"))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        return tokens;
    }

    private static (SqlToken Token, int NextIndex) ReadQuotedToken(
        string sql,
        int start)
    {
        var opener = sql[start];
        var closer = opener == '[' ? ']' : opener;
        var index = start + 1;
        var closed = false;
        while (index < sql.Length)
        {
            if (sql[index] != closer)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closer)
            {
                index += 2;
                continue;
            }

            index++;
            closed = true;
            break;
        }
        if (!closed)
            throw new InvalidDataException("Schema contains unterminated quoted SQL.");

        if (opener == '\'')
        {
            return (new SqlToken(
                    SqlTokenKind.StringLiteral,
                    sql[start..index]),
                index);
        }

        var value = sql[(start + 1)..(index - 1)]
            .Replace(closer.ToString() + closer, closer.ToString(),
                StringComparison.Ordinal)
            .ToLowerInvariant();
        return (new SqlToken(SqlTokenKind.Word, value), index);
    }

    private static bool IsOperatorCharacter(char character) =>
        character is '=' or '<' or '>' or '!' or '|' or '&' or
            '+' or '-' or '*' or '/' or '%' or '~';

    private static bool TryGetCreateBody(
        IReadOnlyList<SqlToken> tokens,
        out IReadOnlyList<SqlToken> body)
    {
        var open = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] == new SqlToken(
                    SqlTokenKind.Punctuation, "("))
            {
                open = index;
                break;
            }
        }
        if (open < 0)
        {
            body = [];
            return false;
        }
        body = tokens.Skip(open + 1).ToArray();
        return true;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record ColumnShape(
        string Name,
        string Type,
        bool NotNull,
        bool PrimaryKey);

    private sealed record ForeignKeyShape(
        string Table,
        string From,
        string To,
        string OnUpdate,
        string OnDelete,
        string Match);

    private sealed record IndexShape(
        string Table,
        string Name,
        bool Unique,
        bool Partial,
        IReadOnlyList<string> Columns,
        string CreateSql);

    private sealed record SqlToken(SqlTokenKind Kind, string Value);

    private enum SqlTokenKind
    {
        Word,
        Number,
        StringLiteral,
        Operator,
        Punctuation
    }
}
