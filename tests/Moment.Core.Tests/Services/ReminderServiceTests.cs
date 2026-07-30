using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.Core.Tests.Services;

public sealed class ReminderServiceTests
{
    [Fact]
    public async Task Create_signals_scheduler_only_after_atomic_save_succeeds()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events);
        var signal = new RecordingSignal(events);
        var service = new ReminderService(repository, signal,
            new FakeClock("2026-07-29T09:00:00+08:00"));

        await service.CreateAsync(TestData.Draft("休息", "2026-07-29T09:20:00+08:00"),
            CancellationToken.None);

        Assert.Equal(["save", "refresh"], events);
    }

    [Fact]
    public async Task Create_does_not_signal_scheduler_when_atomic_save_fails()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events, shouldThrow: true);
        var signal = new RecordingSignal(events);
        var service = new ReminderService(repository, signal,
            new FakeClock("2026-07-29T09:00:00+08:00"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            TestData.Draft("休息", "2026-07-29T09:20:00+08:00"), CancellationToken.None));

        Assert.Equal(["save"], events);
    }

    [Theory]
    [MemberData(nameof(InvalidDrafts))]
    public async Task Create_rejects_invalid_draft_before_repository_or_scheduler(ReminderDraft draft)
    {
        var events = new List<string>();
        var service = new ReminderService(new RecordingRepository(events), new RecordingSignal(events),
            new FakeClock("2026-07-29T09:00:00+08:00"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateAsync(draft, CancellationToken.None));

        Assert.Empty(events);
    }

    [Fact]
    public async Task Edit_rejects_invalid_draft_before_loading_or_mutating_the_repository()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events);
        var existing = TestData.Scheduled("编辑前", "2026-07-29T09:20:00+08:00");
        await repository.AddAsync(existing, CancellationToken.None);
        events.Clear();
        var service = new ReminderService(repository, new RecordingSignal(events),
            new FakeClock("2026-07-29T09:00:00+08:00"));
        var invalid = new ReminderDraft("编辑后", existing.Occurrence.DueAt.AddMinutes(10), (ReminderKind)99,
            ReminderImportance.Normal, null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EditAsync(
            existing.Occurrence.Id, invalid, SeriesScope.OccurrenceOnly, CancellationToken.None));

        Assert.Empty(events);
        Assert.Equal("编辑前", (await repository.GetScheduledReminderAsync(existing.Occurrence.Id, CancellationToken.None))!.Item.Title);
    }

    public static TheoryData<ReminderDraft> InvalidDrafts =>
    [
        new ReminderDraft("无效种类", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            (ReminderKind)99, ReminderImportance.Normal, null),
        new ReminderDraft("无效重要性", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            ReminderKind.Countdown, (ReminderImportance)99, null),
        new ReminderDraft("无效循环种类", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            ReminderKind.Countdown, ReminderImportance.Normal,
            new RecurrenceRule((RecurrenceKind)99, [], new TimeOnly(9, 20))),
        new ReminderDraft("每周无日期", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            ReminderKind.Countdown, ReminderImportance.Normal,
            new RecurrenceRule(RecurrenceKind.Weekly, [], new TimeOnly(9, 20))),
        new ReminderDraft("每天却有日期", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            ReminderKind.Countdown, ReminderImportance.Normal,
            new RecurrenceRule(RecurrenceKind.Daily, [DayOfWeek.Monday], new TimeOnly(9, 20))),
        new ReminderDraft("工作日却有日期", new DateTimeOffset(2026, 7, 29, 9, 20, 0, TimeSpan.FromHours(8)),
            ReminderKind.Countdown, ReminderImportance.Normal,
            new RecurrenceRule(RecurrenceKind.Weekdays, [DayOfWeek.Monday], new TimeOnly(9, 20)))
    ];

    private sealed class RecordingRepository(List<string> events, bool shouldThrow = false) : FakeReminderRepository
    {
        public override Task<ScheduledReminder?> GetScheduledReminderAsync(Guid occurrenceId, CancellationToken ct)
        {
            events.Add("get");
            return base.GetScheduledReminderAsync(occurrenceId, ct);
        }

        public override Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct)
        {
            events.Add("save");
            if (shouldThrow)
            {
                throw new InvalidOperationException("save failed");
            }

            return base.SaveItemWithOccurrenceAsync(item, occurrence, ct);
        }
    }

    private sealed class RecordingSignal(List<string> events) : ISchedulerSignal
    {
        public void Refresh() => events.Add("refresh");
    }
}
