using System.Globalization;
using Hourbit.Core.Domain;
using Hourbit.Core.Search;
using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Data;

public sealed class SqliteItemSearchQueryTests
{
    [Fact]
    public async Task Search_finds_active_reminders_and_todos_by_title_and_excludes_deleted_rows()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "hourbit.db");
        var reminders = await SqliteReminderRepository.OpenAsync(path, default);
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var created = Parse("2026-08-01T00:00:00+08:00");
        var todo = new TodoItem(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "提交季度报告", created, new DateOnly(2026, 9, 5),
            ReminderImportance.Important, false, null);
        var deletedTodo = new TodoItem(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            "删除季度报告", created, null,
            ReminderImportance.Normal, false, null);
        await todos.SaveAsync(todo, default);
        await todos.SaveAsync(deletedTodo, default);
        await todos.DeleteAsync(deletedTodo.Id, created.AddDays(1), default);

        var activeReminder = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000001",
            "季度报告会议", Parse("2026-09-03T09:00:00+08:00"));
        var deletedReminder = await SaveReminderAsync(
            reminders, "20000000-0000-0000-0000-000000000002",
            "删除季度报告会议", Parse("2026-09-04T09:00:00+08:00"));
        await reminders.DeleteAsync(
            deletedReminder.Id, SeriesScope.OccurrenceOnly,
            created.AddDays(1), default);

        var results = await new SqliteItemSearchQuery(path).SearchAsync(
            new ItemSearchFilter("季度报告"), default);

        Assert.Equal([activeReminder.Id, todo.Id], results.Select(row => row.Id));
        Assert.Equal([SearchItemType.Reminder, SearchItemType.Todo],
            results.Select(row => row.Type));
        Assert.Equal(new DateOnly(2026, 9, 3), results[0].LocalDate);
        Assert.Equal(new DateOnly(2026, 9, 5), results[1].LocalDate);
        Assert.All(results, row => Assert.DoesNotContain("删除", row.Title));
    }

    [Fact]
    public async Task Search_is_case_insensitive_trimmed_and_deterministically_orders_undated_last()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "hourbit.db");
        var todos = await SqliteTodoRepository.OpenAsync(path, default);
        var created = Parse("2026-08-01T00:00:00+08:00");
        var dated = new TodoItem(Guid.Parse("30000000-0000-0000-0000-000000000002"),
            "Alpha 计划", created, new DateOnly(2026, 8, 20),
            ReminderImportance.Normal, false, null);
        var undated = new TodoItem(Guid.Parse("30000000-0000-0000-0000-000000000001"),
            "alpha 无日期", created, null,
            ReminderImportance.Normal, false, null);
        await todos.SaveAsync(undated, default);
        await todos.SaveAsync(dated, default);

        var results = await new SqliteItemSearchQuery(path).SearchAsync(
            new ItemSearchFilter("  ALPHA  "), default);

        Assert.Equal([dated.Id, undated.Id], results.Select(row => row.Id));
    }

    private static async Task<ReminderOccurrence> SaveReminderAsync(
        SqliteReminderRepository repository,
        string occurrenceId,
        string title,
        DateTimeOffset dueAt)
    {
        var item = ReminderItem.Create(
            title, ReminderKind.Plan, ReminderImportance.Normal,
            dueAt.AddDays(-1), dueAt);
        var occurrence = new ReminderOccurrence(
            Guid.Parse(occurrenceId), item.Id, dueAt,
            OccurrenceState.Scheduled, null, null);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, default);
        return occurrence;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
