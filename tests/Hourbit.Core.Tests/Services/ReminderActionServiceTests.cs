using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Recurrence;
using Hourbit.Core.Services;
using Hourbit.TestSupport;

namespace Hourbit.Core.Tests.Services;

public sealed class ReminderActionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo ChinaZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    [Fact]
    public async Task Complete_is_idempotent_and_creates_one_next_recurrence()
    {
        var repository = new FakeReminderRepository();
        var current = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        var service = CreateService(repository);

        await service.CompleteAsync(current.Id, CancellationToken.None);
        await service.CompleteAsync(current.Id, CancellationToken.None);

        var updated = await repository.GetScheduledReminderAsync(current.Id, CancellationToken.None);
        var scheduled = await repository.GetScheduledAsync(CancellationToken.None);
        Assert.Equal(OccurrenceState.Completed, updated!.Occurrence.State);
        var next = Assert.Single(scheduled);
        Assert.Equal(Now.AddDays(1).AddMinutes(20), next.Occurrence.DueAt);
    }

    [Fact]
    public async Task Ignore_without_recurrence_creates_no_future_occurrence()
    {
        var repository = new FakeReminderRepository();
        var current = TestData.Scheduled("一次提醒", "2026-07-29T09:20:00+08:00");
        await repository.AddAsync(current, CancellationToken.None);

        await CreateService(repository).IgnoreAsync(current.Occurrence.Id, CancellationToken.None);

        Assert.Equal(OccurrenceState.Ignored,
            (await repository.GetScheduledReminderAsync(current.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Complete_for_a_terminal_occurrence_does_not_create_a_future_occurrence()
    {
        var repository = new FakeReminderRepository();
        var current = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        await repository.ApplyActionAsync(current.Id, OccurrenceState.Completed, Now, null, CancellationToken.None);

        await CreateService(repository).CompleteAsync(current.Id, CancellationToken.None);

        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Complete_calculates_recurrence_in_the_injected_scheduling_zone()
    {
        var repository = new FakeReminderRepository();
        var due = DateTimeOffset.Parse("2026-07-29T09:20:00+08:00");
        var item = new ReminderItem(Guid.NewGuid(), "跨时区循环", ReminderKind.Plan, ReminderImportance.Normal,
            Now.AddDays(-1), RecurrenceRule.Daily(new TimeOnly(10, 0)));
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);
        var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var service = new ReminderActionService(repository, new RecurrenceCalculator(), new NullSignal(),
            new FakeClock(Now), easternZone);

        await service.CompleteAsync(occurrence.Id, CancellationToken.None);

        var next = Assert.Single(await repository.GetScheduledAsync(CancellationToken.None));
        Assert.Equal(DateTimeOffset.Parse("2026-07-29T10:00:00-04:00"), next.Occurrence.DueAt);
    }

    [Fact]
    public async Task Snooze_links_the_new_occurrence_to_its_parent()
    {
        var repository = new FakeReminderRepository();
        var current = TestData.Scheduled("重要提醒", "2026-07-29T09:20:00+08:00", ReminderImportance.Important);
        await repository.AddAsync(current, CancellationToken.None);

        var snoozed = await CreateService(repository).SnoozeAsync(current.Occurrence.Id, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(current.Occurrence.Id, snoozed.SnoozeParentId);
        Assert.Equal(Now.AddMinutes(5), snoozed.DueAt);
        Assert.Equal(OccurrenceState.Snoozed,
            (await repository.GetScheduledReminderAsync(current.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
        Assert.Equal(snoozed.Id, (await repository.GetScheduledReminderAsync(snoozed.Id, CancellationToken.None))!.Occurrence.Id);
    }

    [Fact]
    public async Task Snooze_for_a_missing_occurrence_throws_without_signal_or_mutation()
    {
        var repository = new FakeReminderRepository();
        var signal = new RecordingSignal();
        var service = new ReminderActionService(repository, new RecurrenceCalculator(), signal,
            new FakeClock(Now), ChinaZone);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SnoozeAsync(
            Guid.NewGuid(), TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Equal("Reminder occurrence is not actionable.", exception.Message);
        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
        Assert.Equal(0, signal.RefreshCount);
    }

    [Fact]
    public async Task Snooze_for_a_terminal_occurrence_throws_without_signal_or_mutation()
    {
        var repository = new FakeReminderRepository();
        var current = TestData.Scheduled("已完成提醒", "2026-07-29T09:20:00+08:00", ReminderImportance.Important);
        await repository.AddAsync(current, CancellationToken.None);
        await repository.ApplyActionAsync(current.Occurrence.Id, OccurrenceState.Completed, Now, null, CancellationToken.None);
        var signal = new RecordingSignal();
        var service = new ReminderActionService(repository, new RecurrenceCalculator(), signal,
            new FakeClock(Now), ChinaZone);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SnoozeAsync(
            current.Occurrence.Id, TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Equal("Reminder occurrence is not actionable.", exception.Message);
        Assert.Equal(OccurrenceState.Completed,
            (await repository.GetScheduledReminderAsync(current.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
        Assert.Equal(0, signal.RefreshCount);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task Snooze_rejects_non_ten_minute_delays_for_normal_notifications(int minutes)
    {
        var repository = new FakeReminderRepository();
        var current = TestData.Scheduled("普通提醒", "2026-07-29T09:20:00+08:00");
        await repository.AddAsync(current, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService(repository).SnoozeAsync(
            current.Occurrence.Id, TimeSpan.FromMinutes(minutes), CancellationToken.None));

        Assert.Equal(OccurrenceState.Scheduled,
            (await repository.GetScheduledReminderAsync(current.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
    }

    [Fact]
    public async Task Edit_occurrence_only_changes_the_selected_occurrence()
    {
        var repository = new FakeReminderRepository();
        var selected = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        var later = ReminderOccurrence.Schedule(selected.ItemId, Now.AddDays(1).AddMinutes(20));
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);
        var draft = new ReminderDraft("已编辑", Now.AddHours(1), ReminderKind.Alarm,
            ReminderImportance.Important, RecurrenceRule.Daily(new TimeOnly(10, 0)));

        await new ReminderService(repository, new NullSignal(), new FakeClock(Now)).EditAsync(
            selected.Id, draft, SeriesScope.OccurrenceOnly, CancellationToken.None);

        var changed = await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None);
        var untouched = await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None);
        Assert.Equal("已编辑", changed!.Item.Title);
        Assert.Null(changed.Item.Recurrence);
        Assert.Equal(Now.AddHours(1), changed.Occurrence.DueAt);
        Assert.Equal("循环提醒", untouched!.Item.Title);
    }

    [Fact]
    public async Task Delete_occurrence_only_removes_only_the_selected_occurrence()
    {
        var repository = new FakeReminderRepository();
        var selected = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        var later = ReminderOccurrence.Schedule(selected.ItemId, Now.AddDays(1).AddMinutes(20));
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);

        await new ReminderService(repository, new NullSignal(), new FakeClock(Now)).DeleteAsync(
            selected.Id, SeriesScope.OccurrenceOnly, CancellationToken.None);

        Assert.Null(await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None));
        Assert.Equal(Now, repository.LastDeletedAt);
    }

    [Fact]
    public async Task Edit_this_and_future_replaces_scheduled_tail_and_preserves_past_history()
    {
        var repository = new FakeReminderRepository();
        var selected = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        var past = new ReminderOccurrence(Guid.NewGuid(), selected.ItemId, Now.AddDays(-1), OccurrenceState.Completed, Now.AddDays(-1), null);
        var later = ReminderOccurrence.Schedule(selected.ItemId, Now.AddDays(1).AddMinutes(20));
        await repository.SaveOccurrenceAsync(past, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);
        var draft = new ReminderDraft("新系列", Now.AddHours(1), ReminderKind.Plan,
            ReminderImportance.Important, RecurrenceRule.Weekdays(new TimeOnly(10, 0)));

        await new ReminderService(repository, new NullSignal(), new FakeClock(Now)).EditAsync(
            selected.Id, draft, SeriesScope.ThisAndFuture, CancellationToken.None);

        var history = await repository.GetScheduledReminderAsync(past.Id, CancellationToken.None);
        var replacement = Assert.Single(await repository.GetScheduledAsync(CancellationToken.None));
        Assert.Equal("循环提醒", history!.Item.Title);
        Assert.Equal("新系列", replacement.Item.Title);
        Assert.Equal(RecurrenceKind.Weekdays, replacement.Item.Recurrence!.Kind);
        Assert.Null(await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_this_and_future_removes_scheduled_tail_and_preserves_past_history()
    {
        var repository = new FakeReminderRepository();
        var selected = await AddRecurringReminderAsync(repository, Now.AddMinutes(20));
        var past = new ReminderOccurrence(Guid.NewGuid(), selected.ItemId, Now.AddDays(-1), OccurrenceState.Completed, Now.AddDays(-1), null);
        var later = ReminderOccurrence.Schedule(selected.ItemId, Now.AddDays(1).AddMinutes(20));
        await repository.SaveOccurrenceAsync(past, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);

        await new ReminderService(repository, new NullSignal(), new FakeClock(Now)).DeleteAsync(
            selected.Id, SeriesScope.ThisAndFuture, CancellationToken.None);

        Assert.Equal("循环提醒", (await repository.GetScheduledReminderAsync(past.Id, CancellationToken.None))!.Item.Title);
        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
        Assert.Equal(Now, repository.LastDeletedAt);
    }

    private static ReminderActionService CreateService(FakeReminderRepository repository) =>
        new(repository, new RecurrenceCalculator(), new NullSignal(), new FakeClock(Now), ChinaZone);

    private static async Task<ReminderOccurrence> AddRecurringReminderAsync(FakeReminderRepository repository, DateTimeOffset due)
    {
        var item = new ReminderItem(Guid.NewGuid(), "循环提醒", ReminderKind.Plan, ReminderImportance.Normal,
            Now.AddDays(-1), RecurrenceRule.Daily(new TimeOnly(9, 20)));
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);
        return occurrence;
    }

    private sealed class NullSignal : ISchedulerSignal
    {
        public void Refresh()
        {
        }
    }

    private sealed class RecordingSignal : ISchedulerSignal
    {
        public int RefreshCount { get; private set; }

        public void Refresh() => RefreshCount++;
    }
}
