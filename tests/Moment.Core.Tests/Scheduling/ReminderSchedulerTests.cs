using Moment.Core.Scheduling;
using Moment.TestSupport;

namespace Moment.Core.Tests.Scheduling;

public sealed class ReminderSchedulerTests
{
    [Fact]
    public async Task Refresh_interrupts_wait_and_delivers_new_earlier_occurrence_once()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("later", clock.Now.AddHours(1).ToString("O")));
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);

        await repository.AddAsync(TestData.Scheduled("earlier", clock.Now.AddMinutes(1).ToString("O")));
        scheduler.Refresh();
        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        await sink.WaitForCountAsync(1);

        Assert.Equal("earlier", sink.Deliveries.Single().Item.Title);
    }

    [Fact]
    public async Task Simultaneous_due_occurrences_are_each_delivered()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("first", clock.Now.AddMinutes(1).ToString("O")));
        await repository.AddAsync(TestData.Scheduled("second", clock.Now.AddMinutes(1).ToString("O")));
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);

        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        await sink.WaitForCountAsync(2);

        Assert.Equal(["first", "second"], sink.Deliveries.Select(delivery => delivery.Item.Title).Order());
    }

    [Fact]
    public async Task Duplicate_refreshes_do_not_duplicate_delivery()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("once", clock.Now.AddMinutes(1).ToString("O")));
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);

        scheduler.Refresh();
        scheduler.Refresh();
        scheduler.Refresh();
        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        await sink.WaitForCountAsync(1);
        scheduler.Refresh();

        Assert.Single(sink.Deliveries);
    }

    [Fact]
    public async Task Compare_and_set_allows_only_one_of_two_schedulers_to_deliver()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("once", clock.Now.AddMinutes(1).ToString("O")));
        using var first = new ReminderScheduler(repository, sink, clock);
        using var second = new ReminderScheduler(repository, sink, clock);
        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        await sink.WaitForCountAsync(1);

        Assert.Single(sink.Deliveries);
    }

    [Fact]
    public async Task Start_after_disposal_throws()
    {
        var scheduler = new ReminderScheduler(
            new FakeReminderRepository(), new RecordingReminderSink(),
            new FakeClock("2026-07-29T09:00:00+08:00"));
        scheduler.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Start_with_a_cancelled_token_does_not_run_the_loop()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("not delivered", clock.Now.AddMinutes(1).ToString("O")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var scheduler = new ReminderScheduler(repository, sink, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.StartAsync(cancellation.Token));
        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        Assert.Empty(sink.Deliveries);
    }

    [Fact]
    public async Task Disposal_cancels_a_pending_wait_before_the_reminder_becomes_due()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("not delivered", clock.Now.AddMinutes(1).ToString("O")));
        var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);

        scheduler.Dispose();
        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        Assert.Empty(sink.Deliveries);
    }
}
