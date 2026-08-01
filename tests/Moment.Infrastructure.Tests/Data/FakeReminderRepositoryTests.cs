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

    [Fact]
    public async Task EditAsync_with_occurrence_only_rolls_back_when_the_replacement_id_collides()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("单次冲突", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var selected = ReminderOccurrence.Schedule(item.Id, due);
        var conflicting = ReminderOccurrence.Schedule(item.Id, due.AddHours(1));
        await repository.SaveItemWithOccurrenceAsync(item, selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(conflicting, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.EditAsync(selected.Id, item,
            selected with { Id = conflicting.Id, DueAt = due.AddHours(2) }, SeriesScope.OccurrenceOnly, CancellationToken.None));

        Assert.Equal(item.Id, (await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None))!.Item.Id);
        Assert.NotNull(await repository.GetScheduledReminderAsync(conflicting.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EditAsync_with_this_and_future_rolls_back_when_the_replacement_id_collides()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "未来冲突", ReminderKind.Plan, ReminderImportance.Normal,
            due.AddDays(-1), RecurrenceRule.Daily(new TimeOnly(9, 0)));
        var completed = new ReminderOccurrence(Guid.NewGuid(), item.Id, due.AddDays(-1), OccurrenceState.Completed, due.AddDays(-1), null);
        var selected = ReminderOccurrence.Schedule(item.Id, due);
        var later = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, completed, CancellationToken.None);
        await repository.SaveOccurrenceAsync(selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.EditAsync(selected.Id, item with { Title = "不应保存" },
            selected with { Id = completed.Id }, SeriesScope.ThisAndFuture, CancellationToken.None));

        Assert.Equal("未来冲突", (await repository.GetScheduledReminderAsync(completed.Id, CancellationToken.None))!.Item.Title);
        Assert.NotNull(await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TryMarkFiredAsync_is_compare_and_set_for_scheduled_occurrences()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("假 CAS", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var scheduled = ReminderOccurrence.Schedule(item.Id, due);
        var completed = new ReminderOccurrence(Guid.NewGuid(), item.Id, due.AddHours(1), OccurrenceState.Completed, due, null);
        await repository.SaveItemWithOccurrenceAsync(item, scheduled, CancellationToken.None);
        await repository.SaveOccurrenceAsync(completed, CancellationToken.None);

        Assert.True(await repository.TryMarkFiredAsync(scheduled.Id, due, CancellationToken.None));
        Assert.False(await repository.TryMarkFiredAsync(scheduled.Id, due.AddMinutes(1), CancellationToken.None));
        Assert.False(await repository.TryMarkFiredAsync(completed.Id, due, CancellationToken.None));
        Assert.Equal(OccurrenceState.Fired, (await repository.GetScheduledReminderAsync(scheduled.Id, CancellationToken.None))!.Occurrence.State);
        Assert.Equal(OccurrenceState.Completed, (await repository.GetScheduledReminderAsync(completed.Id, CancellationToken.None))!.Occurrence.State);
    }

    [Fact]
    public async Task GetRecoverableAsync_returns_due_scheduled_and_normal_fired_occurrences_in_due_time_and_id_order()
    {
        var repository = new FakeReminderRepository();
        var through = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(8));
        var normalItem = ReminderItem.Create("普通", ReminderKind.Plan, ReminderImportance.Normal, through.AddDays(-1), through);
        var importantItem = ReminderItem.Create("重要", ReminderKind.Plan, ReminderImportance.Important, through.AddDays(-1), through);
        var fired = new ReminderOccurrence(Guid.Parse("00000000-0000-0000-0000-000000000001"), normalItem.Id,
            through.AddHours(-1), OccurrenceState.Fired, through.AddHours(-1), null);
        var firstScheduled = new ReminderOccurrence(Guid.Parse("00000000-0000-0000-0000-000000000002"), importantItem.Id,
            through, OccurrenceState.Scheduled, null, null);
        var secondScheduled = new ReminderOccurrence(Guid.Parse("00000000-0000-0000-0000-000000000003"), normalItem.Id,
            through, OccurrenceState.Scheduled, null, null);
        var importantFired = new ReminderOccurrence(Guid.NewGuid(), importantItem.Id,
            through.AddHours(-2), OccurrenceState.Fired, through.AddHours(-2), null);
        var future = ReminderOccurrence.Schedule(normalItem.Id, through.AddMinutes(1));
        var completed = new ReminderOccurrence(Guid.NewGuid(), normalItem.Id,
            through.AddHours(-3), OccurrenceState.Completed, through.AddHours(-3), null);
        await repository.SaveItemWithOccurrenceAsync(normalItem, fired, CancellationToken.None);
        await repository.SaveItemWithOccurrenceAsync(importantItem, firstScheduled, CancellationToken.None);
        await repository.SaveOccurrenceAsync(secondScheduled, CancellationToken.None);
        await repository.SaveOccurrenceAsync(importantFired, CancellationToken.None);
        await repository.SaveOccurrenceAsync(future, CancellationToken.None);
        await repository.SaveOccurrenceAsync(completed, CancellationToken.None);

        var recoverable = await repository.GetRecoverableAsync(through, CancellationToken.None);

        Assert.Collection(recoverable,
            reminder => Assert.Equal(fired.Id, reminder.Occurrence.Id),
            reminder => Assert.Equal(firstScheduled.Id, reminder.Occurrence.Id),
            reminder => Assert.Equal(secondScheduled.Id, reminder.Occurrence.Id));
    }

    [Fact]
    public async Task TryTransitionAsync_allows_only_one_concurrent_caller_to_claim_the_expected_state()
    {
        var repository = new FakeReminderRepository();
        var due = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("原子认领", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

        var results = await Task.WhenAll(
            repository.TryTransitionAsync(occurrence.Id, OccurrenceState.Scheduled, OccurrenceState.Fired, due, CancellationToken.None),
            repository.TryTransitionAsync(occurrence.Id, OccurrenceState.Scheduled, OccurrenceState.Fired, due, CancellationToken.None));

        Assert.Equal(1, results.Count(static result => result));
        var stored = await repository.GetScheduledReminderAsync(occurrence.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Fired, stored!.Occurrence.State);
        Assert.Equal(due, stored.Occurrence.HandledAt);
    }
}
