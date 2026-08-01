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
                reader.GetString(1).ToLowerInvariant(),
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
        TryGetCreateBody(TokenizeSql(actualSql), out var actualBody) &&
        TryGetCreateBody(TokenizeSql(CreateTodosTableSql), out var canonicalBody) &&
        actualBody.SequenceEqual(canonicalBody);

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
            throw new InvalidDataException("The todos table contains unterminated quoted SQL.");

        return (new SqlToken(
                opener == '\''
                    ? SqlTokenKind.StringLiteral
                    : SqlTokenKind.QuotedIdentifier,
                sql[start..index]),
            index);
    }

    private static bool IsOperatorCharacter(char character) =>
        character is '=' or '<' or '>' or '!' or '|' or '&' or
            '+' or '-' or '*' or '/' or '%' or '~';

    private static bool TryGetCreateBody(
        IReadOnlyList<SqlToken> tokens,
        out IReadOnlyList<SqlToken> body)
    {
        if (StartsWith(tokens,
                Word("create"), Word("table"), Word("todos"),
                Punctuation("(")))
        {
            body = tokens.Skip(4).ToArray();
            return true;
        }
        if (StartsWith(tokens,
                Word("create"), Word("table"), Word("if"), Word("not"),
                Word("exists"), Word("todos"), Punctuation("(")))
        {
            body = tokens.Skip(7).ToArray();
            return true;
        }

        body = [];
        return false;
    }

    private static bool StartsWith(
        IReadOnlyList<SqlToken> tokens,
        params SqlToken[] prefix) =>
        tokens.Count >= prefix.Length &&
        tokens.Take(prefix.Length).SequenceEqual(prefix);

    private static SqlToken Word(string value) =>
        new(SqlTokenKind.Word, value);

    private static SqlToken Punctuation(string value) =>
        new(SqlTokenKind.Punctuation, value);

    private sealed record ColumnShape(
        string Name,
        string Type,
        bool NotNull,
        bool PrimaryKey);

    private sealed record SqlToken(SqlTokenKind Kind, string Value);

    private enum SqlTokenKind
    {
        Word,
        Number,
        StringLiteral,
        QuotedIdentifier,
        Operator,
        Punctuation
    }
}
