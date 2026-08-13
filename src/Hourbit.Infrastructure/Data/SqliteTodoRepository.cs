using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;

namespace Hourbit.Infrastructure.Data;

public sealed class SqliteTodoRepository : ITodoRepository
{
    private const string SelectColumns =
        "id, title, created_at, due_date, importance, is_completed, completed_at";
    private readonly string _databasePath;

    private SqliteTodoRepository(string databasePath) =>
        _databasePath = databasePath;

    public static async Task<SqliteTodoRepository> OpenAsync(
        string databasePath,
        CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await DatabaseMigrator.MigrateAsync(connection, ct);
        return new SqliteTodoRepository(databasePath);
    }

    public async Task SaveAsync(TodoItem item, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await InsertAsync(connection, null, item, ct);
    }

    public async Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM todos WHERE id = $id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTodo(reader) : null;
    }

    public async Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM todos
            WHERE deleted_at IS NULL
            ORDER BY due_date IS NULL, due_date, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var todos = new List<TodoItem>();
        while (await reader.ReadAsync(ct))
            todos.Add(ReadTodo(reader));
        return todos;
    }

    public async Task UpdateAsync(TodoItem item, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command =
            CreateDetailsUpdateCommand(connection, null, item);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetCompletedAsync(
        Guid id,
        bool isCompleted,
        DateTimeOffset? completedAt,
        CancellationToken ct)
    {
        if (isCompleted != completedAt.HasValue)
        {
            throw new ArgumentException(
                "Completion state and timestamp must agree.",
                nameof(completedAt));
        }

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction =
            connection.BeginTransaction(deferred: false);
        var existing = await GetAsync(connection, transaction, id, ct);
        if (existing is null || existing.IsCompleted == isCompleted)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        _ = new TodoItem(
            existing.Id,
            existing.Title,
            existing.CreatedAt,
            existing.DueDate,
            existing.Importance,
            isCompleted,
            completedAt);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todos
            SET is_completed = $isCompleted,
                completed_at = $completedAt
            WHERE id = $id
              AND is_completed = $expectedState
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue(
            "$isCompleted", isCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt",
            completedAt is null
                ? DBNull.Value
                : Format(completedAt.Value));
        command.Parameters.AddWithValue(
            "$expectedState", isCompleted ? 0 : 1);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Todo completion state changed during the transaction.");
        }
        await transaction.CommitAsync(ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        DeleteAsync(id, DateTimeOffset.UtcNow, ct);

    public async Task DeleteAsync(
        Guid id,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todos
            SET deleted_at = $deletedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$deletedAt", Format(deletedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        TodoItem item,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            INSERT INTO todos(
                id, title, created_at, due_date, importance,
                is_completed, completed_at)
            VALUES (
                $id, $title, $createdAt, $dueDate, $importance,
                $isCompleted, $completedAt);
            """;
        AddParameters(command, item);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken ct) =>
        await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);

    private static async Task<TodoItem?> GetAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        Guid id,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM todos WHERE id = $id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTodo(reader) : null;
    }

    private static SqliteCommand CreateDetailsUpdateCommand(
        SqliteConnection connection,
        DbTransaction? transaction,
        TodoItem item)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            UPDATE todos
            SET title = $title,
                due_date = $dueDate,
                importance = $importance
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$dueDate",
            item.DueDate is null
                ? DBNull.Value
                : Format(item.DueDate.Value));
        command.Parameters.AddWithValue("$importance", (int)item.Importance);
        return command;
    }

    private static void AddParameters(SqliteCommand command, TodoItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$createdAt", Format(item.CreatedAt));
        command.Parameters.AddWithValue("$dueDate",
            item.DueDate is null ? DBNull.Value : Format(item.DueDate.Value));
        command.Parameters.AddWithValue("$importance", (int)item.Importance);
        command.Parameters.AddWithValue("$isCompleted", item.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt",
            item.CompletedAt is null
                ? DBNull.Value
                : Format(item.CompletedAt.Value));
    }

    private static TodoItem ReadTodo(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseDateTimeOffset(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseDateOnly(reader.GetString(3)),
            (ReminderImportance)reader.GetInt32(4),
            reader.GetInt32(5) == 1,
            reader.IsDBNull(6)
                ? null
                : ParseDateTimeOffset(reader.GetString(6)));

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string Format(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateOnly ParseDateOnly(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
