using System.Globalization;
using Hourbit.Core.Analytics;
using Hourbit.Core.Domain;
using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Data;

public sealed class SqliteAnalyticsQueryTests
{
    [Fact]
    public async Task Read_returns_typed_history_at_inclusive_start_exclusive_end_and_optionally_includes_deleted_rows()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var todos = await SqliteTodoRepository.OpenAsync(path, CancellationToken.None);
        var start = Parse("2026-08-01T00:00:00Z");
        var end = Parse("2026-08-02T00:00:00Z");
        var activeTodo = Todo("10000000-0000-0000-0000-000000000001", "活动待办", new DateOnly(2026, 8, 1));
        var deletedTodo = Todo("10000000-0000-0000-0000-000000000002", "删除待办", null, true,
            "2026-08-01T01:00:00Z");
        await todos.SaveAsync(activeTodo, CancellationToken.None);
        await todos.SaveAsync(deletedTodo, CancellationToken.None);
        await todos.DeleteAsync(deletedTodo.Id, Parse("2026-08-01T02:00:00Z"), CancellationToken.None);

        var atStart = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000001", "开始边界",
            ReminderKind.Alarm, start, OccurrenceState.Scheduled);
        _ = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000004", "结束边界",
            ReminderKind.Plan, end, OccurrenceState.Scheduled);
        var completedByAction = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000002", "动作完成",
            ReminderKind.Countdown, start.AddDays(-2), OccurrenceState.Scheduled);
        await reminders.ApplyActionAsync(
            completedByAction.Id, OccurrenceState.Completed,
            start.AddHours(3), null, CancellationToken.None);
        var deleted = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000003", "删除提醒",
            ReminderKind.Plan, start.AddHours(5), OccurrenceState.Scheduled);
        await reminders.DeleteAsync(
            deleted.Id, SeriesScope.OccurrenceOnly,
            start.AddHours(6), CancellationToken.None);

        var query = new SqliteAnalyticsQuery(path);
        var active = await query.ReadAsync(start, end, includeDeleted: false, CancellationToken.None);
        var withDeleted = await query.ReadAsync(start, end, includeDeleted: true, CancellationToken.None);

        Assert.Equal([activeTodo.Id], active.Todos.Select(row => row.TodoId));
        Assert.Equal(
            [completedByAction.Id, atStart.Id],
            active.Reminders.Select(row => row.OccurrenceId));
        var completed = active.Reminders[0];
        Assert.Equal(ReminderKind.Countdown, completed.Kind);
        Assert.Equal(OccurrenceState.Completed, completed.State);
        Assert.Null(completed.DeletedAt);
        var action = Assert.Single(active.Actions);
        Assert.Equal(completedByAction.Id, action.OccurrenceId);
        Assert.Equal(OccurrenceState.Completed, action.State);
        Assert.Equal(start.AddHours(3), action.HandledAt);

        Assert.Equal([activeTodo.Id, deletedTodo.Id], withDeleted.Todos.Select(row => row.TodoId));
        Assert.Equal(
            [completedByAction.Id, atStart.Id, deleted.Id],
            withDeleted.Reminders.Select(row => row.OccurrenceId));
        Assert.Equal(start.AddHours(6), withDeleted.Reminders[^1].DeletedAt);
        Assert.Equal(start.AddHours(2), withDeleted.Todos[^1].DeletedAt);
    }

    [Fact]
    public async Task Read_keeps_all_history_lists_on_one_SQLite_snapshot_during_a_concurrent_delete()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var todos = await SqliteTodoRepository.OpenAsync(path, CancellationToken.None);
        await EnableWalAsync(path);
        await todos.SaveAsync(
            Todo("30000000-0000-0000-0000-000000000001", "快照待办", null),
            CancellationToken.None);
        var due = Parse("2026-08-01T10:00:00Z");
        var occurrence = await SaveReminderAsync(
            reminders, "30000000-0000-0000-0000-000000000002", "快照提醒",
            ReminderKind.Plan, due, OccurrenceState.Scheduled);
        var todosRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new SqliteAnalyticsQuery(
            path,
            async (stage, ct) =>
            {
                if (stage != AnalyticsQueryReadStage.TodosRead)
                    return;
                todosRead.TrySetResult();
                await continueRead.Task.WaitAsync(ct);
            });

        var readTask = query.ReadAsync(
            Parse("2026-08-01T00:00:00Z"), Parse("2026-08-02T00:00:00Z"),
            includeDeleted: false, CancellationToken.None);
        await todosRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await reminders.DeleteAsync(
                    occurrence.Id, SeriesScope.OccurrenceOnly,
                    due.AddHours(1), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            continueRead.TrySetResult();
        }

        var duringDelete = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        var afterDelete = await new SqliteAnalyticsQuery(path).ReadAsync(
            Parse("2026-08-01T00:00:00Z"), Parse("2026-08-02T00:00:00Z"),
            includeDeleted: false, CancellationToken.None);

        Assert.Equal(occurrence.Id, Assert.Single(duringDelete.Reminders).OccurrenceId);
        Assert.Empty(afterDelete.Reminders);
    }

    [Fact]
    public async Task Read_applies_exact_half_open_ticks_after_SQL_candidate_filtering_for_all_timestamp_paths()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var todos = await SqliteTodoRepository.OpenAsync(path, CancellationToken.None);
        var start = Parse("2026-08-01T00:00:00Z");
        var end = Parse("2026-08-02T00:00:00Z");
        var beforeEnd = end.AddTicks(-1);
        var oldDue = start.AddDays(-30);

        var dueBefore = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000001", "到期前一tick",
            ReminderKind.Alarm, beforeEnd, OccurrenceState.Scheduled);
        _ = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000002", "到期正好end",
            ReminderKind.Alarm, end, OccurrenceState.Scheduled);
        var handledBefore = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000003", "处理前一tick",
            ReminderKind.Plan, oldDue, OccurrenceState.Completed,
            beforeEnd.ToOffset(TimeSpan.FromHours(9)));
        _ = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000004", "处理正好end",
            ReminderKind.Plan, oldDue.AddMinutes(1), OccurrenceState.Completed,
            end.ToOffset(TimeSpan.FromHours(-5)));
        var actionBefore = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000005", "动作前一tick",
            ReminderKind.Countdown, oldDue.AddMinutes(2), OccurrenceState.Scheduled);
        await InsertActionAsync(
            path, "75000000-0000-0000-0000-000000000001", actionBefore.Id,
            OccurrenceState.Completed, beforeEnd.ToOffset(TimeSpan.FromHours(13)));
        var actionAtEnd = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000006", "动作正好end",
            ReminderKind.Countdown, oldDue.AddMinutes(3), OccurrenceState.Scheduled);
        await InsertActionAsync(
            path, "75000000-0000-0000-0000-000000000002", actionAtEnd.Id,
            OccurrenceState.Completed, end.ToOffset(TimeSpan.FromHours(-11)));
        var deletedBefore = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000007", "删除前一tick",
            ReminderKind.Plan, oldDue.AddMinutes(4), OccurrenceState.Scheduled);
        await reminders.DeleteAsync(
            deletedBefore.Id, SeriesScope.OccurrenceOnly,
            beforeEnd.ToOffset(TimeSpan.FromHours(14)), CancellationToken.None);
        var deletedAtEnd = await SaveReminderAsync(
            reminders, "72000000-0000-0000-0000-000000000008", "删除正好end",
            ReminderKind.Plan, oldDue.AddMinutes(5), OccurrenceState.Scheduled);
        await reminders.DeleteAsync(
            deletedAtEnd.Id, SeriesScope.OccurrenceOnly,
            end.ToOffset(TimeSpan.FromHours(-12)), CancellationToken.None);

        var completedTodoBefore = Todo(
            "73000000-0000-0000-0000-000000000001", "待办完成前一tick",
            new DateOnly(2026, 1, 1), true,
            beforeEnd.ToOffset(TimeSpan.FromHours(10)).ToString("O", CultureInfo.InvariantCulture));
        var completedTodoAtEnd = Todo(
            "73000000-0000-0000-0000-000000000002", "待办完成正好end",
            new DateOnly(2026, 1, 2), true,
            end.ToOffset(TimeSpan.FromHours(-8)).ToString("O", CultureInfo.InvariantCulture));
        var deletedTodoBefore = Todo(
            "73000000-0000-0000-0000-000000000003", "待办删除前一tick",
            new DateOnly(2026, 1, 3));
        var deletedTodoAtEnd = Todo(
            "73000000-0000-0000-0000-000000000004", "待办删除正好end",
            new DateOnly(2026, 1, 4));
        foreach (var todo in new[]
                 {
                     completedTodoBefore, completedTodoAtEnd,
                     deletedTodoBefore, deletedTodoAtEnd
                 })
        {
            await todos.SaveAsync(todo, CancellationToken.None);
        }
        await todos.DeleteAsync(
            deletedTodoBefore.Id, beforeEnd.ToOffset(TimeSpan.FromHours(12)), CancellationToken.None);
        await todos.DeleteAsync(
            deletedTodoAtEnd.Id, end.ToOffset(TimeSpan.FromHours(-9)), CancellationToken.None);

        var history = await new SqliteAnalyticsQuery(path).ReadAsync(
            start, end, includeDeleted: true, CancellationToken.None);

        Assert.Equal(
            [dueBefore.Id, handledBefore.Id, actionBefore.Id, deletedBefore.Id],
            history.Reminders.Select(row => row.OccurrenceId).Order());
        var action = Assert.Single(history.Actions);
        Assert.Equal(actionBefore.Id, action.OccurrenceId);
        Assert.Equal(beforeEnd, action.HandledAt);
        Assert.Equal(
            [completedTodoBefore.Id, deletedTodoBefore.Id],
            history.Todos.Select(row => row.TodoId).Order());
    }

    [Fact]
    public async Task Read_bounds_a_large_dated_todo_history_but_retains_active_undated_todos()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, CancellationToken.None);
        for (var index = 0; index < 200; index++)
        {
            await todos.SaveAsync(
                new TodoItem(
                    Guid.NewGuid(), $"远期历史 {index}", Parse("2020-01-01T00:00:00Z"),
                    new DateOnly(2020, 1, 1).AddDays(index), ReminderImportance.Normal,
                    false, null),
                CancellationToken.None);
        }
        var inRange = Todo(
            "74000000-0000-0000-0000-000000000001", "范围内",
            new DateOnly(2026, 8, 1));
        var undated = Todo(
            "74000000-0000-0000-0000-000000000002", "活动无日期", null);
        await todos.SaveAsync(inRange, CancellationToken.None);
        await todos.SaveAsync(undated, CancellationToken.None);

        var history = await new SqliteAnalyticsQuery(path).ReadAsync(
            Parse("2026-08-01T00:00:00Z"), Parse("2026-08-02T00:00:00Z"),
            includeDeleted: false, CancellationToken.None);

        Assert.Equal(
            [inRange.Id, undated.Id],
            history.Todos.Select(row => row.TodoId).Order());
    }

    [Fact]
    public async Task Schema_and_action_range_predicate_use_the_canonical_handled_at_index()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        _ = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT a.id, a.occurrence_id, a.state, a.handled_at
            FROM action_log a
            INNER JOIN occurrences o ON o.id = a.occurrence_id
            WHERE ($includeDeleted = 1 OR o.deleted_at IS NULL)
              AND a.handled_at >= $safeStartText
              AND a.handled_at < $safeEndText
            ORDER BY a.handled_at, a.id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$includeDeleted", 1);
        command.Parameters.AddWithValue("$safeStartText", "2026-07-31");
        command.Parameters.AddWithValue("$safeEndText", "2026-08-04");
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains(
            "ix_action_log_handled_at_occurrence_id", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(details, detail => detail.Contains(
            "SCAN a", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Read_honors_pre_cancelled_token()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        _ = await SqliteTodoRepository.OpenAsync(path, CancellationToken.None);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteAnalyticsQuery(path).ReadAsync(
                Parse("2026-08-01T00:00:00Z"), Parse("2026-08-02T00:00:00Z"),
                includeDeleted: true, source.Token));
    }

    private static TodoItem Todo(
        string id,
        string title,
        DateOnly? dueDate,
        bool completed = false,
        string? completedAt = null) =>
        new(
            Guid.Parse(id), title, Parse("2026-07-01T00:00:00Z"), dueDate,
            ReminderImportance.Normal, completed,
            completedAt is null ? null : Parse(completedAt));

    private static async Task<ReminderOccurrence> SaveReminderAsync(
        SqliteReminderRepository repository,
        string occurrenceId,
        string title,
        ReminderKind kind,
        DateTimeOffset dueAt,
        OccurrenceState state,
        DateTimeOffset? handledAt = null)
    {
        var item = ReminderItem.Create(
            title, kind, ReminderImportance.Normal,
            dueAt.AddDays(-10), dueAt);
        var occurrence = new ReminderOccurrence(
            Guid.Parse(occurrenceId), item.Id, dueAt, state,
            state == OccurrenceState.Scheduled ? null : handledAt ?? dueAt.AddMinutes(1), null);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);
        return occurrence;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task EnableWalAsync(string path)
    {
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        Assert.Equal("wal", Convert.ToString(
            await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture));
    }

    private static async Task InsertActionAsync(
        string path,
        string actionId,
        Guid occurrenceId,
        OccurrenceState state,
        DateTimeOffset handledAt)
    {
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_log(id, occurrence_id, state, handled_at)
            VALUES ($id, $occurrenceId, $state, $handledAt);
            """;
        command.Parameters.AddWithValue("$id", actionId);
        command.Parameters.AddWithValue("$occurrenceId", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue(
            "$handledAt", handledAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
