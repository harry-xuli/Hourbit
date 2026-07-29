using Moment.Core.Domain;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

public sealed class FakeReminderRepositoryTests
{
    [Fact]
    public async Task SaveOccurrenceAsync_rejects_duplicate_due_instants_and_missing_items_without_mutating_data()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("假仓储", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveOccurrenceAsync(
            ReminderOccurrence.Schedule(item.Id, due.ToOffset(TimeSpan.Zero)), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveOccurrenceAsync(
            ReminderOccurrence.Schedule(Guid.NewGuid(), due.AddDays(1)), CancellationToken.None));

        Assert.Single(await repository.GetScheduledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApplyActionAsync_with_a_missing_current_occurrence_does_not_save_next_occurrence()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("假动作", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var existing = ReminderOccurrence.Schedule(item.Id, due);
        var next = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, existing, CancellationToken.None);

        await repository.ApplyActionAsync(Guid.NewGuid(), OccurrenceState.Completed, due, next, CancellationToken.None);

        Assert.Null(await repository.GetScheduledReminderAsync(next.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EditAsync_with_a_missing_current_occurrence_does_not_create_a_replacement()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("假编辑", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var existing = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, existing, CancellationToken.None);
        var replacement = ReminderOccurrence.Schedule(item.Id, due.AddHours(1));

        await repository.EditAsync(Guid.NewGuid(), item, replacement, SeriesScope.OccurrenceOnly, CancellationToken.None);

        Assert.Null(await repository.GetScheduledReminderAsync(replacement.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetScheduledReminderAsync(existing.Id, CancellationToken.None));
    }
}
