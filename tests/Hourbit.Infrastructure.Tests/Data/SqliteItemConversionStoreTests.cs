using System.Globalization;
using Microsoft.Data.Sqlite;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Recurrence;
using Hourbit.Core.Services;
using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Data;

public sealed class SqliteItemConversionStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 2, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset DueAt =
        new(2026, 8, 2, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task ItemConversion_todo_to_reminder_inserts_destination_before_deleting_source()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var source = PendingTodo("原待办");
        await todos.SaveAsync(source, default);
        var (item, occurrence) = ScheduledDestination(
            "已转提醒", RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await ExecuteAsync(path, $"""
            CREATE TRIGGER require_reminder_destination
            BEFORE DELETE ON todos
            WHEN OLD.id = '{source.Id:D}' AND NOT EXISTS (
                SELECT 1 FROM occurrences WHERE id = '{occurrence.Id:D}'
            )
            BEGIN
                SELECT RAISE(ABORT, 'destination reminder missing');
            END;
            """);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        var result = await store.ConvertTodoToReminderAsync(
            new TodoToReminderConversion(source, item, occurrence), default);

        Assert.True(result.SchedulingChanged);
        Assert.Null(await todos.GetAsync(source.Id, default));
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var stored = await reminders.GetScheduledReminderAsync(
            occurrence.Id, default);
        Assert.Equal(item, stored!.Item);
        Assert.Equal(occurrence, stored.Occurrence);
        Assert.Equal(RecurrenceKind.Daily, stored.Item.Recurrence!.Kind);
    }

    [Fact]
    public async Task ItemConversion_completed_todo_creates_a_completed_reminder_that_is_not_scheduled()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var completedAt = CreatedAt.AddHours(1);
        var source = new TodoItem(
            Guid.NewGuid(), "已完成待办", CreatedAt, null,
            ReminderImportance.Important, true, completedAt);
        await todos.SaveAsync(source, default);
        var item = new ReminderItem(
            Guid.NewGuid(), source.Title, ReminderKind.Plan,
            source.Importance, source.CreatedAt,
            RecurrenceRule.Weekdays(new TimeOnly(10, 0)));
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), item.Id, DueAt, OccurrenceState.Completed,
            completedAt, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        var result = await store.ConvertTodoToReminderAsync(
            new TodoToReminderConversion(source, item, occurrence), default);

        Assert.False(result.SchedulingChanged);
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        Assert.Empty(await reminders.GetScheduledAsync(default));
        var stored = await reminders.GetScheduledReminderAsync(
            occurrence.Id, default);
        Assert.Equal(OccurrenceState.Completed, stored!.Occurrence.State);
        Assert.Equal(completedAt, stored.Occurrence.HandledAt);
    }

    [Fact]
    public async Task ItemConversion_rejects_a_soft_deleted_todo_source()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var source = PendingTodo("已删除待办源");
        await todos.SaveAsync(source, default);
        await todos.DeleteAsync(source.Id, CreatedAt.AddMinutes(1), default);
        var (item, occurrence) = ScheduledDestination("不应创建", null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConvertTodoToReminderAsync(
                new TodoToReminderConversion(source, item, occurrence), default));

        Assert.Equal(1, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id AND deleted_at IS NOT NULL;",
            ("$id", source.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id;",
            ("$id", occurrence.Id.ToString("D"))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItemConversion_reminder_to_dated_or_undated_todo_inserts_destination_before_source_removal(
        bool isDated)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(OccurrenceState.Scheduled, null, null);
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        DateOnly? dueDate = isDated ? new DateOnly(2026, 8, 5) : null;
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt,
            dueDate, source.Item.Importance, false, null);
        await ExecuteAsync(path, $"""
            CREATE TRIGGER require_todo_destination
            BEFORE DELETE ON occurrences
            WHEN OLD.id = '{source.Occurrence.Id:D}' AND NOT EXISTS (
                SELECT 1 FROM todos WHERE id = '{destination.Id:D}'
            )
            BEGIN
                SELECT RAISE(ABORT, 'destination todo missing');
            END;
            """);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        var result = await store.ConvertReminderToTodoAsync(
            new ReminderToTodoConversion(
                source, destination, SeriesScope.OccurrenceOnly, null),
            default);

        Assert.True(result.SchedulingChanged);
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        Assert.Equal(destination, await todos.GetAsync(destination.Id, default));
        Assert.Null(await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default));
        Assert.Null(await reminders.GetItemAsync(source.Item.Id, default));
    }

    [Fact]
    public async Task ItemConversion_rejects_a_soft_deleted_reminder_source()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(OccurrenceState.Scheduled, null, null);
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        await reminders.DeleteAsync(
            source.Occurrence.Id, SeriesScope.OccurrenceOnly,
            CreatedAt.AddMinutes(1), default);
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt,
            null, source.Item.Importance, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly, null),
                default));

        Assert.Equal(1, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id AND deleted_at IS NOT NULL;",
            ("$id", source.Occurrence.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_occurrence_only_continues_a_recurring_series_without_duplicate_due_instants()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var continuation = ReminderOccurrence.Schedule(
            source.Item.Id, DueAt.AddDays(1));
        await reminders.SaveOccurrenceAsync(continuation, default);
        var duplicateRequest = continuation with { Id = Guid.NewGuid() };
        var destination = new TodoItem(
            Guid.NewGuid(), "单次转待办", source.Item.CreatedAt, null,
            ReminderImportance.Normal, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        var result = await store.ConvertReminderToTodoAsync(
            new ReminderToTodoConversion(
                source, destination, SeriesScope.OccurrenceOnly,
                duplicateRequest), default);

        Assert.True(result.SchedulingChanged);
        Assert.Null(await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default));
        var remaining = Assert.Single(await reminders.GetScheduledAsync(default));
        Assert.Equal(continuation.Id, remaining.Occurrence.Id);
        Assert.Equal(RecurrenceKind.Daily, remaining.Item.Recurrence!.Kind);
        Assert.Equal(1, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE item_id = $id AND due_at_utc = $value;",
            ("$id", source.Item.Id.ToString("D")),
            ("$value", FormatUtc(DueAt.AddDays(1)))));
    }

    [Fact]
    public async Task ItemConversion_occurrence_only_inserts_an_active_continuation_beside_deleted_history()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var deletedContinuation = ReminderOccurrence.Schedule(
            source.Item.Id, DueAt.AddDays(1));
        await reminders.SaveOccurrenceAsync(deletedContinuation, default);
        await reminders.DeleteAsync(
            deletedContinuation.Id, SeriesScope.OccurrenceOnly,
            DueAt.AddMinutes(1), default);
        var activeContinuation = deletedContinuation with { Id = Guid.NewGuid() };
        var destination = new TodoItem(
            Guid.NewGuid(), "单次转待办", source.Item.CreatedAt, null,
            ReminderImportance.Normal, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await store.ConvertReminderToTodoAsync(
            new ReminderToTodoConversion(
                source, destination, SeriesScope.OccurrenceOnly,
                activeContinuation), default);

        var remaining = Assert.Single(await reminders.GetScheduledAsync(default));
        Assert.Equal(activeContinuation.Id, remaining.Occurrence.Id);
        Assert.Equal(2, await ScalarIntAsync(path, """
            SELECT COUNT(*)
            FROM occurrences
            WHERE item_id = $id AND due_at_utc = $value;
            """,
            ("$id", source.Item.Id.ToString("D")),
            ("$value", FormatUtc(activeContinuation.DueAt))));
        Assert.Equal(1, await ScalarIntAsync(path, """
            SELECT COUNT(*)
            FROM occurrences
            WHERE id = $id AND deleted_at IS NOT NULL;
            """, ("$id", deletedContinuation.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_occurrence_only_rejects_an_actionable_recurring_source_without_a_continuation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt, null,
            source.Item.Importance, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly, null),
                default));

        Assert.NotNull(await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_this_and_future_preserves_past_occurrence_and_action_history()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var item = new ReminderItem(
            Guid.NewGuid(), "循环提醒", ReminderKind.Plan,
            ReminderImportance.Important, CreatedAt,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        var selected = ReminderOccurrence.Schedule(item.Id, DueAt);
        await reminders.SaveItemWithOccurrenceAsync(item, selected, default);
        var past = ReminderOccurrence.Schedule(item.Id, DueAt.AddDays(-1));
        await reminders.SaveOccurrenceAsync(past, default);
        var pastHandledAt = DueAt.AddDays(-1).AddMinutes(5);
        await reminders.ApplyActionAsync(
            past.Id, OccurrenceState.Completed, pastHandledAt, null, default);
        var later = ReminderOccurrence.Schedule(item.Id, DueAt.AddDays(1));
        await reminders.SaveOccurrenceAsync(later, default);
        var source = new ScheduledReminder(item, selected);
        var destination = new TodoItem(
            Guid.NewGuid(), "系列转待办", item.CreatedAt, null,
            ReminderImportance.Normal, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        var result = await store.ConvertReminderToTodoAsync(
            new ReminderToTodoConversion(
                source, destination, SeriesScope.ThisAndFuture, null),
            default);

        Assert.True(result.SchedulingChanged);
        Assert.Null(await reminders.GetScheduledReminderAsync(selected.Id, default));
        Assert.Null(await reminders.GetScheduledReminderAsync(later.Id, default));
        var preserved = await reminders.GetScheduledReminderAsync(past.Id, default);
        Assert.Equal(OccurrenceState.Completed, preserved!.Occurrence.State);
        Assert.Null(preserved.Item.Recurrence);
        Assert.Equal(1, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM action_log WHERE occurrence_id = $id;",
            ("$id", past.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_reminder_to_todo_rejects_a_same_state_handled_at_change()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(OccurrenceState.Scheduled, null, null);
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var changedHandledAt = DueAt.AddMinutes(-5);
        await reminders.SetOccurrenceStateAsync(
            source.Occurrence.Id, OccurrenceState.Scheduled,
            changedHandledAt, default);
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt, null,
            source.Item.Importance, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly, null),
                default));

        var persisted = await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default);
        Assert.Equal(OccurrenceState.Scheduled, persisted!.Occurrence.State);
        Assert.Equal(changedHandledAt, persisted.Occurrence.HandledAt);
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_reminder_to_todo_rejects_a_changed_recurrence_rule()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        await ExecuteAsync(path, $"""
            UPDATE recurrence_rules
            SET kind = {(int)RecurrenceKind.Weekly},
                days_of_week = '{(int)DayOfWeek.Monday}',
                time = '11:00:00.0000000'
            WHERE item_id = '{source.Item.Id:D}';
            """);
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt, null,
            source.Item.Importance, false, null);
        var continuation = ReminderOccurrence.Schedule(
            source.Item.Id, DueAt.AddDays(1));
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly,
                    continuation), default));

        var persisted = await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default);
        Assert.Equal(RecurrenceKind.Weekly, persisted!.Item.Recurrence!.Kind);
        Assert.Equal(new TimeOnly(11, 0), persisted.Item.Recurrence.Time);
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id;",
            ("$id", continuation.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_reminder_to_todo_rejects_changed_item_fields()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(OccurrenceState.Scheduled, null, null);
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var changedCreatedAt = source.Item.CreatedAt.AddMinutes(1);
        await ExecuteAsync(path, $"""
            UPDATE items
            SET title = '并发后提醒',
                kind = {(int)ReminderKind.Alarm},
                importance = {(int)ReminderImportance.Normal},
                created_at = '{changedCreatedAt.ToString("O", CultureInfo.InvariantCulture)}'
            WHERE id = '{source.Item.Id:D}';
            """);
        var destination = new TodoItem(
            Guid.NewGuid(), source.Item.Title, source.Item.CreatedAt, null,
            source.Item.Importance, false, null);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly, null),
                default));

        var persisted = await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default);
        Assert.Equal("并发后提醒", persisted!.Item.Title);
        Assert.Equal(ReminderKind.Alarm, persisted.Item.Kind);
        Assert.Equal(ReminderImportance.Normal, persisted.Item.Importance);
        Assert.Equal(changedCreatedAt, persisted.Item.CreatedAt);
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_todo_to_reminder_rolls_back_destination_when_source_delete_fails()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var source = PendingTodo("删除失败仍保留");
        await todos.SaveAsync(source, default);
        var (item, occurrence) = ScheduledDestination(
            "不应残留", RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await ExecuteAsync(path, $"""
            CREATE TRIGGER fail_todo_source_delete
            BEFORE DELETE ON todos WHEN OLD.id = '{source.Id:D}'
            BEGIN
                SELECT RAISE(ABORT, 'injected source delete failure');
            END;
            """);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.ConvertTodoToReminderAsync(
                new TodoToReminderConversion(source, item, occurrence), default));

        Assert.Equal(source, await todos.GetAsync(source.Id, default));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM items WHERE id = $id;",
            ("$id", item.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id;",
            ("$id", occurrence.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM recurrence_rules WHERE item_id = $id;",
            ("$id", item.Id.ToString("D"))));
    }

    [Fact]
    public async Task ItemConversion_todo_to_reminder_rejects_an_exact_source_snapshot_that_was_edited_and_completed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var source = PendingTodo("并发前");
        await todos.SaveAsync(source, default);
        var completedAt = CreatedAt.AddMinutes(30);
        var mutatingTodos = new SnapshotThenMutateTodoRepository(
            todos,
            async snapshot =>
            {
                await todos.UpdateAsync(new TodoItem(
                    snapshot.Id, "并发后", snapshot.CreatedAt,
                    new DateOnly(2026, 8, 8), ReminderImportance.Important,
                    snapshot.IsCompleted, snapshot.CompletedAt), default);
                await todos.SetCompletedAsync(
                    snapshot.Id, true, completedAt, default);
            });
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);
        var signal = new RecordingSignal();
        var service = new TodoService(
            mutatingTodos, reminders, store, new RecurrenceCalculator(),
            signal, new FakeClock(CreatedAt.AddMinutes(10)),
            TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertToReminderAsync(source.Id,
                new ReminderDraft(
                    source.Title, DueAt, ReminderKind.Alarm,
                    source.Importance,
                    RecurrenceRule.Daily(new TimeOnly(10, 0))),
                default));

        var persisted = await todos.GetAsync(source.Id, default);
        Assert.Equal("并发后", persisted!.Title);
        Assert.Equal(new DateOnly(2026, 8, 8), persisted.DueDate);
        Assert.Equal(ReminderImportance.Important, persisted.Importance);
        Assert.True(persisted.IsCompleted);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Empty(await reminders.GetScheduledAsync(default));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM items;"));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences;"));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM recurrence_rules;"));
        Assert.Equal(0, signal.RefreshCount);
    }

    [Fact]
    public async Task ItemConversion_reminder_to_todo_rolls_back_destination_and_continuation_when_source_delete_fails()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        await reminders.SaveItemWithOccurrenceAsync(
            source.Item, source.Occurrence, default);
        var destination = new TodoItem(
            Guid.NewGuid(), "不应残留", source.Item.CreatedAt, null,
            ReminderImportance.Normal, false, null);
        var continuation = ReminderOccurrence.Schedule(
            source.Item.Id, DueAt.AddDays(1));
        await ExecuteAsync(path, $"""
            CREATE TRIGGER fail_reminder_source_delete
            BEFORE DELETE ON occurrences
            WHEN OLD.id = '{source.Occurrence.Id:D}'
            BEGIN
                SELECT RAISE(ABORT, 'injected source delete failure');
            END;
            """);
        var store = await SqliteItemConversionStore.OpenAsync(path, default);

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.ConvertReminderToTodoAsync(
                new ReminderToTodoConversion(
                    source, destination, SeriesScope.OccurrenceOnly,
                    continuation), default));

        Assert.NotNull(await reminders.GetScheduledReminderAsync(
            source.Occurrence.Id, default));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM todos WHERE id = $id;",
            ("$id", destination.Id.ToString("D"))));
        Assert.Equal(0, await ScalarIntAsync(path,
            "SELECT COUNT(*) FROM occurrences WHERE id = $id;",
            ("$id", continuation.Id.ToString("D"))));
    }

    private static TodoItem PendingTodo(string title) =>
        new(Guid.NewGuid(), title, CreatedAt, null,
            ReminderImportance.Normal, false, null);

    private static ScheduledReminder Reminder(
        OccurrenceState state,
        DateTimeOffset? handledAt,
        RecurrenceRule? recurrence)
    {
        var item = new ReminderItem(
            Guid.NewGuid(), "原提醒", ReminderKind.Plan,
            ReminderImportance.Important, CreatedAt, recurrence);
        return new ScheduledReminder(item, new ReminderOccurrence(
            Guid.NewGuid(), item.Id, DueAt, state, handledAt, null));
    }

    private static (ReminderItem Item, ReminderOccurrence Occurrence)
        ScheduledDestination(string title, RecurrenceRule? recurrence)
    {
        var item = new ReminderItem(
            Guid.NewGuid(), title, ReminderKind.Alarm,
            ReminderImportance.Important, CreatedAt, recurrence);
        return (item, ReminderOccurrence.Schedule(item.Id, DueAt));
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(
        string path,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed class SnapshotThenMutateTodoRepository(
        ITodoRepository inner,
        Func<TodoItem, Task> mutate) : ITodoRepository
    {
        private int _hasMutated;

        public Task SaveAsync(TodoItem item, CancellationToken ct) =>
            inner.SaveAsync(item, ct);

        public async Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
        {
            var snapshot = await inner.GetAsync(id, ct);
            if (snapshot is not null &&
                Interlocked.Exchange(ref _hasMutated, 1) == 0)
            {
                await mutate(snapshot);
            }
            return snapshot;
        }

        public Task<IReadOnlyList<TodoItem>> GetAllAsync(
            CancellationToken ct) => inner.GetAllAsync(ct);

        public Task UpdateAsync(TodoItem item, CancellationToken ct) =>
            inner.UpdateAsync(item, ct);

        public Task SetCompletedAsync(
            Guid id,
            bool isCompleted,
            DateTimeOffset? completedAt,
            CancellationToken ct) =>
            inner.SetCompletedAsync(id, isCompleted, completedAt, ct);

        public Task DeleteAsync(Guid id, DateTimeOffset deletedAt,
            CancellationToken ct) =>
            inner.DeleteAsync(id, deletedAt, ct);
    }

    private sealed class RecordingSignal : ISchedulerSignal
    {
        public int RefreshCount { get; private set; }

        public void Refresh() => RefreshCount++;
    }
}
