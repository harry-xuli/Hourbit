using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Infrastructure.Data;
using Moment.TestSupport;
using System.IO;

namespace Moment.App.Tests.Data;

public sealed class SqliteTimelineQueryTests
{
    [Fact]
    public async Task Query_uses_inclusive_start_and_exclusive_end_for_offset_local_date()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-test", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        var item = ReminderItem.Create("边界", ReminderKind.Plan, ReminderImportance.Normal,
            DateTimeOffset.Parse("2026-07-29T00:00:00+08:00"),
            DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));
        var atStart = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));
        var atEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-07-31T00:00:00+08:00"));
        await repository.SaveItemWithOccurrenceAsync(item, atStart, CancellationToken.None);
        await repository.SaveOccurrenceAsync(atEnd, CancellationToken.None);

        var snapshot = await new SqliteTimelineQuery(path).GetTimelineAsync(
            new DateOnly(2026, 7, 30), zone, CancellationToken.None);

        var row = Assert.Single(snapshot.Reminders);
        Assert.Equal(atStart.Id, row.OccurrenceId);
        Assert.Equal(TimeSpan.FromHours(8), row.DueAt.Offset);
    }

    [Fact]
    public async Task Query_computes_a_23_hour_utc_window_across_spring_DST()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var zone = CreateEasternTestZone();
        var item = ReminderItem.Create("夏令时", ReminderKind.Alarm, ReminderImportance.Important,
            DateTimeOffset.Parse("2026-03-07T00:00:00-05:00"),
            DateTimeOffset.Parse("2026-03-08T00:00:00-05:00"));
        var atStart = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-08T00:00:00-05:00"));
        var beforeEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-08T23:59:59-04:00"));
        var atEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-09T00:00:00-04:00"));
        await repository.SaveItemWithOccurrenceAsync(item, atStart, CancellationToken.None);
        await repository.SaveOccurrenceAsync(beforeEnd, CancellationToken.None);
        await repository.SaveOccurrenceAsync(atEnd, CancellationToken.None);

        var snapshot = await new SqliteTimelineQuery(path).GetTimelineAsync(
            new DateOnly(2026, 3, 8), zone, CancellationToken.None);

        Assert.Equal(
            [atStart.Id, beforeEnd.Id],
            snapshot.Reminders.Select(row => row.OccurrenceId));
    }

    [Fact]
    public async Task Query_returns_all_todos_in_action_order_and_counts_completions_by_local_day()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var reminders = await SqliteReminderRepository.OpenAsync(
            path, CancellationToken.None);
        var todos = await SqliteTodoRepository.OpenAsync(
            path, CancellationToken.None);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-todo-query", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        var createdAt = DateTimeOffset.Parse("2026-07-20T09:00:00+08:00");
        var overdueFirst = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var overdueSecond = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var today = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var future = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var undated = Guid.Parse("00000000-0000-0000-0000-000000000005");
        var completedToday = Guid.Parse("00000000-0000-0000-0000-000000000006");
        var completedYesterday = Guid.Parse("00000000-0000-0000-0000-000000000007");
        foreach (var todo in new[]
                 {
                     Todo(overdueSecond, "逾期二", createdAt, new DateOnly(2026, 7, 29)),
                     Todo(undated, "无日期", createdAt, null),
                     Todo(future, "未来", createdAt, new DateOnly(2026, 7, 31)),
                     Todo(today, "今天", createdAt, new DateOnly(2026, 7, 30)),
                     Todo(overdueFirst, "逾期一", createdAt, new DateOnly(2026, 7, 29)),
                     Todo(completedToday, "今日完成", createdAt, null, true,
                         DateTimeOffset.Parse("2026-07-30T00:05:00+08:00")),
                     Todo(completedYesterday, "昨日完成", createdAt, null, true,
                         DateTimeOffset.Parse("2026-07-29T23:59:59+08:00"))
                 })
        {
            await todos.SaveAsync(todo, CancellationToken.None);
        }

        await SaveReminderAsync(
            reminders,
            "跨日后完成",
            DateTimeOffset.Parse("2026-07-29T22:00:00+08:00"),
            OccurrenceState.Completed,
            DateTimeOffset.Parse("2026-07-30T00:10:00+08:00"));
        await SaveReminderAsync(
            reminders,
            "昨日完成",
            DateTimeOffset.Parse("2026-07-29T21:00:00+08:00"),
            OccurrenceState.Completed,
            DateTimeOffset.Parse("2026-07-29T23:59:59+08:00"));

        var snapshot = await new SqliteTimelineQuery(path).GetTimelineAsync(
            new DateOnly(2026, 7, 30), zone, CancellationToken.None);

        Assert.Equal(
            [overdueFirst, overdueSecond, today, future, undated],
            snapshot.Todos.Where(todo => !todo.IsCompleted).Select(todo => todo.TodoId));
        Assert.Equal(7, snapshot.Todos.Count);
        Assert.Empty(snapshot.Reminders);
        Assert.Equal(1, snapshot.TodosCompletedToday);
        Assert.Equal(1, snapshot.RemindersCompletedToday);
    }

    private static TodoItem Todo(
        Guid id,
        string title,
        DateTimeOffset createdAt,
        DateOnly? dueDate,
        bool isCompleted = false,
        DateTimeOffset? completedAt = null) =>
        new(id, title, createdAt, dueDate, ReminderImportance.Normal,
            isCompleted, completedAt);

    private static async Task SaveReminderAsync(
        SqliteReminderRepository repository,
        string title,
        DateTimeOffset dueAt,
        OccurrenceState state,
        DateTimeOffset handledAt)
    {
        var item = ReminderItem.Create(
            title, ReminderKind.Plan, ReminderImportance.Normal,
            dueAt.AddDays(-1), dueAt);
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), item.Id, dueAt, state, handledAt, null);
        await repository.SaveItemWithOccurrenceAsync(
            item, occurrence, CancellationToken.None);
    }

    private static TimeZoneInfo CreateEasternTestZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), daylightStart, daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Eastern-test", TimeSpan.FromHours(-5), "Eastern-test",
            "Eastern-test", "Eastern-test DST", [rule]);
    }
}
