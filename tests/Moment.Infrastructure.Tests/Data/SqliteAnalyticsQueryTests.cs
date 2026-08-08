using System.Globalization;
using Moment.Core.Analytics;
using Moment.Core.Domain;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

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
        OccurrenceState state)
    {
        var item = ReminderItem.Create(
            title, kind, ReminderImportance.Normal,
            dueAt.AddDays(-10), dueAt);
        var occurrence = new ReminderOccurrence(
            Guid.Parse(occurrenceId), item.Id, dueAt, state,
            state == OccurrenceState.Scheduled ? null : dueAt.AddMinutes(1), null);
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
}
