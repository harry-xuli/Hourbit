using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Scheduling;
using Moment.TestSupport;

namespace Moment.Core.Tests.Scheduling;

public sealed class ReminderRecoveryServiceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-01T20:04:00+08:00");

    [Theory]
    [InlineData(-4, -59)]
    [InlineData(-5, 0)]
    public async Task Normal_scheduled_reminders_up_to_five_minutes_late_fire_immediately(
        int minutes, int seconds)
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled(
            "within grace", Now.AddMinutes(minutes).AddSeconds(seconds).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 1, Missed: 0, Failed: 0), result);
        Assert.Equal(OccurrenceState.Fired,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Equal([reminder], sink.Deliveries);
        Assert.Empty(sink.MissedSummaries);
    }

    [Fact]
    public async Task Normal_scheduled_reminder_more_than_five_minutes_late_by_one_tick_is_missed()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled(
            "one tick late",
            Now.Subtract(TimeSpan.FromMinutes(5)).AddTicks(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 0, Missed: 1, Failed: 0), result);
        Assert.Equal(OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Empty(sink.Deliveries);
        Assert.Equal([reminder], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Normal_1900_reminder_recovered_at_2004_is_persisted_as_missed()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled("reported scenario", "2026-08-01T19:00:00+08:00");
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 0, Missed: 1, Failed: 0), result);
        Assert.Equal(OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Equal([reminder], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Important_scheduled_reminder_fires_even_when_hours_late()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled(
            "important", Now.AddHours(-8).ToString("O"), ReminderImportance.Important);
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 1, Missed: 0, Failed: 0), result);
        Assert.Equal(OccurrenceState.Fired,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Equal([reminder], sink.Deliveries);
        Assert.Empty(sink.MissedSummaries);
    }

    [Fact]
    public async Task Normal_fired_reminder_becomes_missed_only_after_five_unhandled_minutes()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var expired = AsFired(
            TestData.Scheduled("expired", Now.AddHours(-1).ToString("O")),
            Now.Subtract(TimeSpan.FromMinutes(5)).AddTicks(-1));
        var boundary = AsFired(
            TestData.Scheduled("boundary", Now.AddHours(-1).ToString("O")),
            Now.Subtract(TimeSpan.FromMinutes(5)));
        await repository.AddAsync(expired);
        await repository.AddAsync(boundary);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 0, Missed: 1, Failed: 0), result);
        Assert.Equal(OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(expired.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Equal(OccurrenceState.Fired,
            (await repository.GetScheduledReminderAsync(boundary.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
        Assert.Equal([expired], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Concurrent_recovery_services_deliver_each_occurrence_only_once()
    {
        var repository = new CapturedSnapshotRepository(expectedSnapshots: 2);
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled("once", Now.AddMinutes(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var first = new ReminderRecoveryService(repository, sink, sink);
        var second = new ReminderRecoveryService(repository, sink, sink);

        var results = await Task.WhenAll(
            first.RecoverAsync(Now, CancellationToken.None),
            second.RecoverAsync(Now, CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.Fired));
        Assert.Equal(0, results.Sum(result => result.Missed));
        Assert.Equal(0, results.Sum(result => result.Failed));
        Assert.Equal([reminder], sink.Deliveries);
    }

    [Fact]
    public async Task Concurrent_recovery_services_summarize_each_missed_occurrence_only_once()
    {
        var repository = new CapturedSnapshotRepository(expectedSnapshots: 2);
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled("once", Now.AddHours(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var first = new ReminderRecoveryService(repository, sink, sink);
        var second = new ReminderRecoveryService(repository, sink, sink);

        var results = await Task.WhenAll(
            first.RecoverAsync(Now, CancellationToken.None),
            second.RecoverAsync(Now, CancellationToken.None));

        Assert.Equal(0, results.Sum(result => result.Fired));
        Assert.Equal(1, results.Sum(result => result.Missed));
        Assert.Equal(0, results.Sum(result => result.Failed));
        Assert.Equal([reminder], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Repeated_recovery_on_one_service_does_not_duplicate_a_missed_summary()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var reminder = TestData.Scheduled("once", Now.AddHours(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var results = await Task.WhenAll(
            service.RecoverAsync(Now, CancellationToken.None),
            service.RecoverAsync(Now, CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.Missed));
        Assert.Equal([reminder], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Newly_claimed_missed_reminders_are_sent_in_one_aggregate_summary()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        var first = TestData.Scheduled("first", Now.AddHours(-2).ToString("O"));
        var second = TestData.Scheduled("second", Now.AddHours(-1).ToString("O"));
        await repository.AddAsync(first);
        await repository.AddAsync(second);
        var service = new ReminderRecoveryService(repository, sink, sink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 0, Missed: 2, Failed: 0), result);
        Assert.Equal([first, second], Assert.Single(sink.MissedSummaries));
    }

    [Fact]
    public async Task Delivery_failure_is_counted_and_does_not_abandon_later_rows()
    {
        var repository = new FakeReminderRepository();
        var summarySink = new RecordingReminderSink();
        var sink = new ThrowFirstDeliverySink();
        var first = TestData.Scheduled("fails", Now.AddMinutes(-2).ToString("O"));
        var second = TestData.Scheduled("continues", Now.AddMinutes(-1).ToString("O"));
        await repository.AddAsync(first);
        await repository.AddAsync(second);
        var service = new ReminderRecoveryService(repository, sink, summarySink);

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 2, Missed: 0, Failed: 1), result);
        Assert.Equal([second], sink.Deliveries);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_delivery_failure()
    {
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        await repository.AddAsync(TestData.Scheduled("cancelled", Now.ToString("O")));
        var service = new ReminderRecoveryService(repository, sink, sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RecoverAsync(Now, cancellation.Token));

        Assert.Empty(sink.Deliveries);
        Assert.Empty(sink.MissedSummaries);
    }

    private static ScheduledReminder AsFired(
        ScheduledReminder reminder, DateTimeOffset firedAt) =>
        reminder with
        {
            Occurrence = reminder.Occurrence with
            {
                State = OccurrenceState.Fired,
                HandledAt = firedAt
            }
        };

    private sealed class CapturedSnapshotRepository(int expectedSnapshots) : FakeReminderRepository
    {
        private readonly TaskCompletionSource _allSnapshotsCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _snapshotCount;

        public override async Task<IReadOnlyList<ScheduledReminder>> GetRecoverableAsync(
            DateTimeOffset through, CancellationToken ct)
        {
            var snapshot = await base.GetRecoverableAsync(through, ct);
            if (Interlocked.Increment(ref _snapshotCount) == expectedSnapshots)
            {
                _allSnapshotsCaptured.TrySetResult();
            }

            await _allSnapshotsCaptured.Task.WaitAsync(ct);
            return snapshot;
        }
    }

    private sealed class ThrowFirstDeliverySink : IReminderSink
    {
        private bool _throw = true;
        private readonly List<ScheduledReminder> _deliveries = [];

        public IReadOnlyList<ScheduledReminder> Deliveries => _deliveries;

        public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_throw)
            {
                _throw = false;
                throw new InvalidOperationException("Delivery failed.");
            }

            _deliveries.Add(reminder);
            return Task.CompletedTask;
        }

        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
