using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Scheduling;
using Hourbit.TestSupport;

namespace Hourbit.Core.Tests.Scheduling;

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
        var expected = new InvalidOperationException("Delivery failed.");
        var sink = new ThrowFirstDeliverySink(expected);
        var first = TestData.Scheduled("fails", Now.AddMinutes(-2).ToString("O"));
        var second = TestData.Scheduled("continues", Now.AddMinutes(-1).ToString("O"));
        await repository.AddAsync(first);
        await repository.AddAsync(second);
        var service = new ReminderRecoveryService(repository, sink, summarySink);
        var reported = new List<Exception>();
        service.RecoveryFailed += reported.Add;

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 1, Missed: 0, Failed: 1), result);
        Assert.Equal([second], sink.Deliveries);
        Assert.Equal([expected], reported);
        Assert.Equal(OccurrenceState.DeliveryFailed,
            (await repository.GetScheduledReminderAsync(first.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
    }

    [Fact]
    public async Task Summary_failure_is_reported_without_rolling_back_missed_state()
    {
        var repository = new FakeReminderRepository();
        var reminderSink = new RecordingReminderSink();
        var expected = new InvalidOperationException("Summary failed.");
        var summarySink = new ThrowingSummarySink(expected);
        var reminder = TestData.Scheduled("missed", Now.AddHours(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, reminderSink, summarySink);
        var reported = new List<Exception>();
        service.RecoveryFailed += reported.Add;

        var result = await service.RecoverAsync(Now, CancellationToken.None);

        Assert.Equal(new ReminderRecoveryResult(Fired: 0, Missed: 1, Failed: 1), result);
        Assert.Equal([expected], reported);
        Assert.Equal([reminder], Assert.Single(summarySink.Attempts));
        Assert.Equal(OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
    }

    [Fact]
    public async Task Cancellation_during_delivery_is_not_reported_and_leaves_recoverable_delivery_claim()
    {
        var repository = new FakeReminderRepository();
        var summarySink = new RecordingReminderSink();
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingDeliverySink(cancellation);
        var reminder = TestData.Scheduled("cancelled", Now.AddMinutes(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, sink, summarySink);
        var reported = new List<Exception>();
        service.RecoveryFailed += reported.Add;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RecoverAsync(Now, cancellation.Token));

        Assert.Empty(reported);
        Assert.Equal(OccurrenceState.Delivering,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
    }

    [Fact]
    public async Task Cancellation_during_summary_is_not_reported_and_preserves_missed_state()
    {
        var repository = new FakeReminderRepository();
        var reminderSink = new RecordingReminderSink();
        using var cancellation = new CancellationTokenSource();
        var summarySink = new CancellingSummarySink(cancellation);
        var reminder = TestData.Scheduled("missed", Now.AddHours(-1).ToString("O"));
        await repository.AddAsync(reminder);
        var service = new ReminderRecoveryService(repository, reminderSink, summarySink);
        var reported = new List<Exception>();
        service.RecoveryFailed += reported.Add;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RecoverAsync(Now, cancellation.Token));

        Assert.Empty(reported);
        Assert.Equal(OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(reminder.Occurrence.Id, CancellationToken.None))!
            .Occurrence.State);
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

    private sealed class ThrowFirstDeliverySink(Exception failure) : IReminderSink
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
                throw failure;
            }

            _deliveries.Add(reminder);
            return Task.CompletedTask;
        }

        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingSummarySink(Exception failure) : IReminderRecoverySummarySink
    {
        private readonly List<IReadOnlyList<ScheduledReminder>> _attempts = [];
        public IReadOnlyList<IReadOnlyList<ScheduledReminder>> Attempts => _attempts;

        public Task SendMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)
        {
            _attempts.Add(reminders);
            return Task.FromException(failure);
        }
    }

    private sealed class CancellingDeliverySink(
        CancellationTokenSource cancellation) : IReminderSink
    {
        public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct)
        {
            cancellation.Cancel();
            return Task.FromCanceled(ct);
        }

        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class CancellingSummarySink(
        CancellationTokenSource cancellation) : IReminderRecoverySummarySink
    {
        public Task SendMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)
        {
            cancellation.Cancel();
            return Task.FromCanceled(ct);
        }
    }
}
