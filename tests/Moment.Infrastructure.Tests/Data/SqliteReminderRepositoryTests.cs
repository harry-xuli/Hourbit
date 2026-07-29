using Moment.Core.Domain;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

public sealed class SqliteReminderRepositoryTests
{
    [Fact]
    public async Task SaveItemWithOccurrence_is_atomic_and_round_trips()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(
            Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var created = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("会议", ReminderKind.Plan,
            ReminderImportance.Important, created, created.AddHours(1));
        var occurrence = ReminderOccurrence.Schedule(item.Id, created.AddHours(1));

        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

        var scheduled = await repository.GetScheduledAsync(CancellationToken.None);
        Assert.Single(scheduled);
        Assert.Equal(item.Id, scheduled[0].Occurrence.ItemId);
    }

    [Fact]
    public async Task EditAsync_with_occurrence_only_leaves_the_item_and_later_occurrences_unchanged()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var due = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "原计划", ReminderKind.Plan, ReminderImportance.Normal,
            due.AddDays(-1), RecurrenceRule.Daily(TimeOnly.FromDateTime(due.DateTime)));
        var selected = ReminderOccurrence.Schedule(item.Id, due);
        var later = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);
        var editedItem = item with { Title = "不应影响系列" };
        var editedOccurrence = selected with { DueAt = due.AddHours(2) };

        await repository.EditAsync(selected.Id, editedItem, editedOccurrence, SeriesScope.OccurrenceOnly, CancellationToken.None);

        var storedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
        var storedSelected = await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None);
        var storedLater = await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None);
        Assert.Equal("原计划", storedItem!.Title);
        Assert.Equal(due.AddHours(2), storedSelected!.Occurrence.DueAt);
        Assert.Equal(due.AddDays(1), storedLater!.Occurrence.DueAt);
    }

    [Fact]
    public async Task DeleteAsync_with_this_and_future_keeps_completed_history()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var selectedDue = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "每日计划", ReminderKind.Plan, ReminderImportance.Normal,
            selectedDue.AddDays(-2), RecurrenceRule.Daily(TimeOnly.FromDateTime(selectedDue.DateTime)));
        var past = new ReminderOccurrence(Guid.NewGuid(), item.Id, selectedDue.AddDays(-1), OccurrenceState.Completed,
            selectedDue.AddDays(-1).AddMinutes(1), null);
        var selected = ReminderOccurrence.Schedule(item.Id, selectedDue);
        var later = ReminderOccurrence.Schedule(item.Id, selectedDue.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, past, CancellationToken.None);
        await repository.SaveOccurrenceAsync(selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);

        await repository.DeleteAsync(selected.Id, SeriesScope.ThisAndFuture, CancellationToken.None);

        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
        var history = await repository.GetScheduledReminderAsync(past.Id, CancellationToken.None);
        var storedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Completed, history!.Occurrence.State);
        Assert.Null(storedItem!.Recurrence);
    }
}
