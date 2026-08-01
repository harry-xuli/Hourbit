using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

public sealed class SqliteTodoRepositoryTests
{
    private const string CanonicalTodosSql = """
        CREATE TABLE todos (
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

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 1, 9, 10, 11, TimeSpan.FromHours(8));

    [Fact]
    public async Task Save_and_get_round_trip_all_fields_using_invariant_storage_formats()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteTodoRepository.OpenAsync(path, default);
        var completedAt = CreatedAt.AddHours(2);
        var todo = new TodoItem(
            Guid.NewGuid(), "  持久化待办  ", CreatedAt,
            new DateOnly(2026, 8, 5), ReminderImportance.Important,
            true, completedAt);

        await repository.SaveAsync(todo, default);

        var stored = await repository.GetAsync(todo.Id, default);
        Assert.Equal(todo, stored);
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT created_at, due_date, completed_at FROM todos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", todo.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            reader.GetString(0));
        Assert.Equal("2026-08-05", reader.GetString(1));
        Assert.Equal(completedAt.ToString("O", CultureInfo.InvariantCulture),
            reader.GetString(2));
    }

    [Fact]
    public async Task Crud_and_completion_operations_are_consistent()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteTodoRepository.OpenAsync(
            Path.Combine(temp.Path, "moment.db"), default);
        var todo = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000010"),
            "初始", new DateOnly(2026, 8, 8));
        await repository.SaveAsync(todo, default);
        var updated = new TodoItem(
            todo.Id, "  已编辑  ", todo.CreatedAt,
            null, ReminderImportance.Important, false, null);

        await repository.UpdateAsync(updated, default);
        Assert.Equal(updated, await repository.GetAsync(todo.Id, default));

        var completedAt = CreatedAt.AddDays(1);
        await repository.SetCompletedAsync(todo.Id, true, completedAt, default);
        var completed = await repository.GetAsync(todo.Id, default);
        Assert.True(completed!.IsCompleted);
        Assert.Equal(completedAt, completed.CompletedAt);

        await repository.SetCompletedAsync(todo.Id, false, null, default);
        var reopened = await repository.GetAsync(todo.Id, default);
        Assert.False(reopened!.IsCompleted);
        Assert.Null(reopened.CompletedAt);

        await repository.DeleteAsync(todo.Id, default);
        Assert.Null(await repository.GetAsync(todo.Id, default));
    }

    [Fact]
    public async Task GetAll_returns_dated_then_undated_todos_in_due_date_and_id_order()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteTodoRepository.OpenAsync(
            Path.Combine(temp.Path, "moment.db"), default);
        var first = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "同日第二", new DateOnly(2026, 8, 3));
        var second = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "同日第一", new DateOnly(2026, 8, 3));
        var earlier = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000004"),
            "更早", new DateOnly(2026, 8, 2));
        var undated = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            "无日期", null);
        foreach (var todo in new[] { first, undated, earlier, second })
            await repository.SaveAsync(todo, default);

        var all = await repository.GetAllAsync(default);

        Assert.Equal(
            new[] { earlier.Id, second.Id, first.Id, undated.Id },
            all.Select(static todo => todo.Id));
    }

    [Fact]
    public async Task Save_rejects_duplicate_ids_without_replacing_the_existing_todo()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteTodoRepository.OpenAsync(
            Path.Combine(temp.Path, "moment.db"), default);
        var original = PendingTodo(Guid.NewGuid(), "原始", null);
        await repository.SaveAsync(original, default);

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveAsync(
            new TodoItem(original.Id, "替换", CreatedAt, null,
                ReminderImportance.Important, false, null), default));

        Assert.Equal(original, await repository.GetAsync(original.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fake_and_sqlite_repositories_have_matching_crud_and_ordering_behavior(
        bool useFake)
    {
        using var temp = new TempDirectory();
        ITodoRepository repository = useFake
            ? new FakeTodoRepository()
            : await SqliteTodoRepository.OpenAsync(
                Path.Combine(temp.Path, "moment.db"), default);
        var dated = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "有日期", new DateOnly(2026, 8, 9));
        var undated = PendingTodo(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "无日期", null);

        await repository.SaveAsync(undated, default);
        await repository.SaveAsync(dated, default);
        await repository.UpdateAsync(new TodoItem(
            dated.Id, "已编辑", dated.CreatedAt, dated.DueDate,
            ReminderImportance.Important, false, null), default);
        await repository.SetCompletedAsync(
            dated.Id, true, CreatedAt.AddMinutes(1), default);

        var all = await repository.GetAllAsync(default);
        Assert.Equal(new[] { dated.Id, undated.Id },
            all.Select(static todo => todo.Id));
        Assert.True(all[0].IsCompleted);
        Assert.Equal("已编辑", all[0].Title);

        await repository.DeleteAsync(undated.Id, default);
        Assert.Single(await repository.GetAllAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edit_from_a_stale_snapshot_does_not_revert_a_concurrent_completion(
        bool useFake)
    {
        using var temp = new TempDirectory();
        ITodoRepository repository = useFake
            ? new FakeTodoRepository()
            : await SqliteTodoRepository.OpenAsync(
                Path.Combine(temp.Path, "moment.db"), default);
        var pending = PendingTodo(Guid.NewGuid(), "编辑前", null);
        await repository.SaveAsync(pending, default);
        var staleEdit = new TodoItem(
            pending.Id, "编辑后", pending.CreatedAt,
            new DateOnly(2026, 8, 9), ReminderImportance.Important,
            false, null);
        var completedAt = CreatedAt.AddMinutes(30);

        await repository.SetCompletedAsync(
            pending.Id, true, completedAt, default);
        await repository.UpdateAsync(staleEdit, default);

        var persisted = await repository.GetAsync(pending.Id, default);
        Assert.Equal("编辑后", persisted!.Title);
        Assert.Equal(new DateOnly(2026, 8, 9), persisted.DueDate);
        Assert.Equal(ReminderImportance.Important, persisted.Importance);
        Assert.True(persisted.IsCompleted);
        Assert.Equal(completedAt, persisted.CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_transition_keeps_the_first_timestamp_and_explicit_uncomplete_is_idempotent(
        bool useFake)
    {
        using var temp = new TempDirectory();
        ITodoRepository repository = useFake
            ? new FakeTodoRepository()
            : await SqliteTodoRepository.OpenAsync(
                Path.Combine(temp.Path, "moment.db"), default);
        var pending = PendingTodo(Guid.NewGuid(), "完成一次", null);
        await repository.SaveAsync(pending, default);
        var first = CreatedAt.AddMinutes(10);
        var second = CreatedAt.AddMinutes(20);

        await repository.SetCompletedAsync(pending.Id, true, first, default);
        await repository.SetCompletedAsync(pending.Id, true, second, default);

        var completed = await repository.GetAsync(pending.Id, default);
        Assert.True(completed!.IsCompleted);
        Assert.Equal(first, completed.CompletedAt);

        await repository.SetCompletedAsync(pending.Id, false, null, default);
        await repository.SetCompletedAsync(pending.Id, false, null, default);
        var reopened = await repository.GetAsync(pending.Id, default);
        Assert.False(reopened!.IsCompleted);
        Assert.Null(reopened.CompletedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Migration_upgrades_existing_databases_without_losing_reminder_or_action_rows(
        int version)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var fixture = await CreateLegacyDatabaseAsync(path, version);

        var repository = await SqliteTodoRepository.OpenAsync(path, default);
        await repository.SaveAsync(PendingTodo(Guid.NewGuid(), "升级后待办", null), default);

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM items WHERE id = $id;", fixture.ItemId));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id;", fixture.OccurrenceId));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM action_log WHERE id = $id;", fixture.ActionId));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_to_version_three_is_idempotent()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);

        await DatabaseMigrator.MigrateAsync(connection, default);
        await DatabaseMigrator.MigrateAsync(connection, default);

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'todos';"));
    }

    [Fact]
    public async Task Todo_rows_never_enter_reminder_scheduler_queries()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        await todos.SaveAsync(PendingTodo(
            Guid.NewGuid(), "绝不提醒", new DateOnly(2026, 8, 1)), default);
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);

        Assert.Empty(await reminders.GetScheduledAsync(default));
        Assert.Empty(await reminders.GetDueAsync(CreatedAt.AddYears(1), default));
    }

    [Fact]
    public async Task Migration_rejects_a_version_three_marker_when_todos_is_missing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, "DROP TABLE todos;");

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseMigrator.MigrateAsync(connection, default));

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    public static TheoryData<string> MalformedTodosSchemas =>
        new()
        {
            CanonicalTodosSql.Replace(
                    "completed_at TEXT NULL,", string.Empty,
                    StringComparison.Ordinal)
                .Replace(
                    "CHECK(\n        (is_completed = 0 AND completed_at IS NULL) OR\n        (is_completed = 1 AND completed_at IS NOT NULL)\n    )",
                    "CHECK(is_completed IN (0, 1))",
                    StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "title TEXT NOT NULL", "title TEXT NULL",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "title TEXT NOT NULL", "title TEXT NOT NULL DEFAULT 'x'",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "id TEXT PRIMARY KEY", "id TEXT",
                StringComparison.Ordinal),
            CanonicalTodosSql
                .Replace(
                    "id TEXT PRIMARY KEY", "id TEXT",
                    StringComparison.Ordinal)
                .Replace(
                    "completed_at TEXT NULL", "completed_at TEXT PRIMARY KEY",
                    StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "created_at TEXT NOT NULL", "created_at BLOB NOT NULL",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                " CHECK(length(trim(title)) BETWEEN 1 AND 200)", string.Empty,
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "due_date IS NULL OR (", "1 OR (",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "importance INTEGER NOT NULL CHECK(importance IN (0, 1))",
                "importance INTEGER NOT NULL",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "is_completed INTEGER NOT NULL CHECK(is_completed IN (0, 1))",
                "is_completed INTEGER NOT NULL",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "CHECK(\n        (is_completed = 0 AND completed_at IS NULL) OR\n        (is_completed = 1 AND completed_at IS NOT NULL)\n    )",
                "CHECK(1)",
                StringComparison.Ordinal)
        };

    public static TheoryData<string> QuotedLiteralCorruptions =>
        new()
        {
            CanonicalTodosSql.Replace(
                "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'",
                "'[0-9] [0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'",
                StringComparison.Ordinal),
            CanonicalTodosSql.Replace(
                "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'",
                "'[0-9];[0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'",
                StringComparison.Ordinal)
        };

    [Theory]
    [MemberData(nameof(MalformedTodosSchemas))]
    public async Task Migration_rejects_a_version_three_marker_with_a_malformed_todos_table(
        string malformedSql)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, $"DROP TABLE todos; {malformedSql}");

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseMigrator.MigrateAsync(connection, default));

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Theory]
    [MemberData(nameof(QuotedLiteralCorruptions))]
    public async Task Migration_rejects_whitespace_or_semicolons_inside_the_due_date_glob_literal(
        string malformedSql)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, $"DROP TABLE todos; {malformedSql}");

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseMigrator.MigrateAsync(connection, default));

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_rejects_a_malformed_preexisting_todos_table_without_adding_the_marker()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, """
            DELETE FROM schema_info WHERE version = 3;
            DROP TABLE todos;
            CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT NOT NULL);
            """);

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseMigrator.MigrateAsync(connection, default));

        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_recovers_a_canonical_preexisting_todos_table_by_adding_one_marker()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, $"""
            DELETE FROM schema_info WHERE version = 3;
            DROP TABLE todos;
            {CanonicalTodosSql}
            """);

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_accepts_canonical_sql_with_token_case_and_formatting_variations()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        var reformattedSql = CanonicalTodosSql
            .Replace("CREATE TABLE todos", "cReAtE\n\tTaBlE   todos",
                StringComparison.Ordinal)
            .Replace("title TEXT NOT NULL", "title\n  text\tNoT NuLl",
                StringComparison.Ordinal)
            .Replace("importance IN", "IMPORTANCE\n in",
                StringComparison.Ordinal);
        await ExecuteAsync(path, $"""
            DELETE FROM schema_info WHERE version = 3;
            DROP TABLE todos;
            {reformattedSql}
            """);

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_accepts_uppercase_unquoted_primary_key_identifier()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        var mixedCaseSql = CanonicalTodosSql
            .Replace("id TEXT PRIMARY KEY", "ID TEXT PRIMARY KEY",
                StringComparison.Ordinal);
        await ExecuteAsync(path, $"""
            DELETE FROM schema_info WHERE version = 3;
            DROP TABLE todos;
            {mixedCaseSql}
            """);

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    [Fact]
    public async Task Migration_rejects_duplicate_version_three_markers_without_changing_them()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        await InitializeVersionThreeAsync(path);
        await ExecuteAsync(path, "INSERT INTO schema_info(version) VALUES (3);");

        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseMigrator.MigrateAsync(connection, default));

        Assert.Equal(2, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;"));
    }

    private static TodoItem PendingTodo(Guid id, string title, DateOnly? dueDate) =>
        new(id, title, CreatedAt, dueDate,
            ReminderImportance.Normal, false, null);

    private static async Task InitializeVersionThreeAsync(string path)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(Guid ItemId, Guid OccurrenceId, Guid ActionId)>
        CreateLegacyDatabaseAsync(string path, int version)
    {
        var itemId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var due = CreatedAt.AddHours(1);
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await using var command = connection.CreateCommand();
        var utcColumn = version == 1
            ? string.Empty
            : ", due_at_utc TEXT NOT NULL";
        var utcInsertColumn = version == 1 ? string.Empty : ", due_at_utc";
        var utcInsertValue = version == 1 ? string.Empty : ", $dueAtUtc";
        command.CommandText = $"""
            CREATE TABLE schema_info (version INTEGER NOT NULL);
            CREATE TABLE items (id TEXT PRIMARY KEY, title TEXT NOT NULL, kind INTEGER NOT NULL, importance INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE occurrences (
                id TEXT PRIMARY KEY,
                item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                due_at TEXT NOT NULL{utcColumn},
                state INTEGER NOT NULL,
                handled_at TEXT NULL,
                snooze_parent_id TEXT NULL,
                UNIQUE(item_id, due_at));
            CREATE TABLE recurrence_rules (item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE, kind INTEGER NOT NULL, days_of_week TEXT NOT NULL, time TEXT NOT NULL);
            CREATE TABLE action_log (id TEXT PRIMARY KEY, occurrence_id TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE, state INTEGER NOT NULL, handled_at TEXT NOT NULL);
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info(version) VALUES (1);
            {(version == 2 ? "INSERT INTO schema_info(version) VALUES (2);" : string.Empty)}
            INSERT INTO items(id, title, kind, importance, created_at) VALUES ($itemId, '保留提醒', 2, 0, $createdAt);
            INSERT INTO occurrences(id, item_id, due_at{utcInsertColumn}, state, handled_at, snooze_parent_id)
                VALUES ($occurrenceId, $itemId, $dueAt{utcInsertValue}, 2, $handledAt, NULL);
            INSERT INTO action_log(id, occurrence_id, state, handled_at)
                VALUES ($actionId, $occurrenceId, 2, $handledAt);
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        command.Parameters.AddWithValue("$occurrenceId", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$actionId", actionId.ToString("D"));
        command.Parameters.AddWithValue("$createdAt",
            CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$dueAt",
            due.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$dueAtUtc",
            due.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$handledAt",
            due.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
        return (itemId, occurrenceId, actionId);
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        string sql,
        Guid? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id.HasValue)
            command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }
}
