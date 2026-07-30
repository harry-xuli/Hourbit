using Moment.Core.Abstractions;
using Moment.Core.Domain;
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

    private sealed class RecordingRepository(List<string> events, bool shouldThrow = false) : FakeReminderRepository
    {
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
