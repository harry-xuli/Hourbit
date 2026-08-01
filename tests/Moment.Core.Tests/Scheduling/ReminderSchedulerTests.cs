using Moment.Core.Abstractions;
using Moment.Core.Domain;
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

    [Fact]
    public async Task Stop_then_start_reuses_the_scheduler_and_delivers_due_work()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new RecordingReminderSink();
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);

        await scheduler.StopAsync(CancellationToken.None);
        await repository.AddAsync(
            TestData.Scheduled("after restore", clock.Now.ToString("O")));
        Assert.Empty(sink.Deliveries);

        await scheduler.StartAsync(CancellationToken.None);
        await sink.WaitForCountAsync(1);

        Assert.Equal("after restore", Assert.Single(sink.Deliveries).Item.Title);
    }

    [Fact]
    public async Task Start_waits_for_in_progress_stop_before_starting_next_loop()
    {
        var repository = new BlockingScheduledQueryRepository();
        using var scheduler = new ReminderScheduler(
            repository,
            new RecordingReminderSink(),
            new FakeClock("2026-07-29T09:00:00+08:00"));
        await scheduler.StartAsync(CancellationToken.None);
        await repository.FirstQueryEntered.Task;

        var stop = scheduler.StopAsync(CancellationToken.None);
        var restart = scheduler.StartAsync(CancellationToken.None);
        await Task.Yield();

        Assert.False(restart.IsCompleted);

        repository.ReleaseFirstQuery.TrySetResult();
        await stop;
        await restart;
        await repository.SecondQueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, repository.QueryCount);
    }

    [Fact]
    public async Task Caller_cancelled_completed_loop_can_be_restarted()
    {
        using var cancellation = new CancellationTokenSource();
        using var scheduler = new ReminderScheduler(
            new FakeReminderRepository(),
            new RecordingReminderSink(),
            new FakeClock("2026-07-29T09:00:00+08:00"));
        await scheduler.StartAsync(cancellation.Token);
        var cancelledLoop = scheduler.Completion;

        cancellation.Cancel();
        await cancelledLoop;
        await scheduler.StartAsync(CancellationToken.None);

        Assert.NotSame(cancelledLoop, scheduler.Completion);
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sink_failure_is_reported_without_stopping_later_delivery_or_faulting_disposal()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new FakeReminderRepository();
        var sink = new ThrowOnceThenRecordingSink();
        var failure = new TaskCompletionSource<SchedulerDeliveryFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        await repository.AddAsync(TestData.Scheduled("fails", clock.Now.AddMinutes(1).ToString("O")));
        await repository.AddAsync(TestData.Scheduled("later", clock.Now.AddMinutes(2).ToString("O")));
        var scheduler = new ReminderScheduler(repository, sink, clock);
        scheduler.DeliveryFailed += observed => failure.TrySetResult(observed);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);

            clock.AdvanceBy(TimeSpan.FromMinutes(1));
            await sink.WaitForFirstAttemptAsync();
            var observed = await failure.Task;

            Assert.Equal("fails", observed.Reminder.Item.Title);
            Assert.IsType<InvalidOperationException>(observed.Exception);
            Assert.False(scheduler.Completion.IsFaulted);

            clock.AdvanceBy(TimeSpan.FromMinutes(1));
            await sink.WaitForCountAsync(1);

            Assert.Equal("later", sink.Deliveries.Single().Item.Title);
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task Normal_fired_occurrence_becomes_missed_only_after_five_unhandled_minutes()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new ObservingReminderRepository();
        var fired = TestData.Scheduled("unhandled", clock.Now.ToString("O"));
        fired = fired with
        {
            Occurrence = fired.Occurrence with
            {
                State = OccurrenceState.Fired,
                HandledAt = clock.Now
            }
        };
        await repository.AddAsync(fired);
        using var scheduler = new ReminderScheduler(repository, new RecordingReminderSink(), clock);
        await scheduler.StartAsync(CancellationToken.None);
        await repository.WaitForRecoverableQueryCountAsync(1);

        clock.AdvanceBy(TimeSpan.FromMinutes(5));
        await repository.WaitForRecoverableQueryCountAsync(2);

        var atDeadline = await repository.GetScheduledReminderAsync(
            fired.Occurrence.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Fired, atDeadline!.Occurrence.State);

        clock.AdvanceBy(TimeSpan.FromTicks(1));
        await repository.WaitForMissedTransitionAsync();

        var expired = await repository.GetScheduledReminderAsync(
            fired.Occurrence.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Missed, expired!.Occurrence.State);
        Assert.Equal(clock.Now, expired.Occurrence.HandledAt);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("ignore")]
    [InlineData("snooze")]
    [InlineData("delete")]
    [InlineData("important")]
    public async Task Action_or_importance_prevents_fired_occurrence_from_becoming_missed(
        string prevention)
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new ObservingReminderRepository();
        var importance = prevention == "important"
            ? ReminderImportance.Important
            : ReminderImportance.Normal;
        var fired = TestData.Scheduled("protected", clock.Now.ToString("O"), importance);
        fired = fired with
        {
            Occurrence = fired.Occurrence with
            {
                State = OccurrenceState.Fired,
                HandledAt = clock.Now
            }
        };
        var probe = TestData.Scheduled("probe", clock.Now.AddMinutes(6).ToString("O"));
        await repository.AddAsync(fired);
        await repository.AddAsync(probe);
        var sink = new RecordingReminderSink();
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        await scheduler.StartAsync(CancellationToken.None);
        await repository.WaitForRecoverableQueryCountAsync(1);

        switch (prevention)
        {
            case "complete":
                await repository.ApplyActionAsync(
                    fired.Occurrence.Id, OccurrenceState.Completed, clock.Now, null,
                    CancellationToken.None);
                break;
            case "ignore":
                await repository.ApplyActionAsync(
                    fired.Occurrence.Id, OccurrenceState.Ignored, clock.Now, null,
                    CancellationToken.None);
                break;
            case "snooze":
                await repository.ApplyActionAsync(
                    fired.Occurrence.Id, OccurrenceState.Snoozed, clock.Now, null,
                    CancellationToken.None);
                break;
            case "delete":
                await repository.DeleteAsync(
                    fired.Occurrence.Id, SeriesScope.OccurrenceOnly, CancellationToken.None);
                break;
        }

        clock.AdvanceBy(TimeSpan.FromMinutes(6));
        await sink.WaitForCountAsync(1);

        var protectedOccurrence = await repository.GetScheduledReminderAsync(
            fired.Occurrence.Id, CancellationToken.None);
        var expectedState = prevention switch
        {
            "complete" => OccurrenceState.Completed,
            "ignore" => OccurrenceState.Ignored,
            "snooze" => OccurrenceState.Snoozed,
            "important" => OccurrenceState.Fired,
            _ => (OccurrenceState?)null
        };
        Assert.Equal(expectedState, protectedOccurrence?.Occurrence.State);
        Assert.False(repository.MissedTransitionCommitted);
        Assert.Equal("probe", Assert.Single(sink.Deliveries).Item.Title);
    }

    [Fact]
    public async Task Each_committed_transition_raises_state_changed_once_and_observer_failure_does_not_stop_loop()
    {
        var clock = new FakeClock("2026-07-29T09:00:00+08:00");
        var repository = new ObservingReminderRepository();
        var sink = new RecordingReminderSink();
        var first = TestData.Scheduled("first", clock.Now.AddMinutes(1).ToString("O"));
        var later = TestData.Scheduled("later", clock.Now.AddMinutes(7).ToString("O"));
        await repository.AddAsync(first);
        await repository.AddAsync(later);
        using var scheduler = new ReminderScheduler(repository, sink, clock);
        var stateChanges = 0;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.StateChanged += (_, _) => throw new InvalidOperationException("observer failed");
        scheduler.StateChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref stateChanges) >= 3)
            {
                changed.TrySetResult();
            }
        };
        await scheduler.StartAsync(CancellationToken.None);

        clock.AdvanceBy(TimeSpan.FromMinutes(1));
        await sink.WaitForCountAsync(1);
        Assert.Equal(1, Volatile.Read(ref stateChanges));

        clock.AdvanceBy(TimeSpan.FromMinutes(5));
        clock.AdvanceBy(TimeSpan.FromTicks(1));
        await repository.WaitForMissedTransitionAsync();

        clock.AdvanceBy(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        await sink.WaitForCountAsync(2);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.Refresh();
        scheduler.Refresh();

        Assert.Equal(3, Volatile.Read(ref stateChanges));
        Assert.Equal(
            OccurrenceState.Missed,
            (await repository.GetScheduledReminderAsync(
                first.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
        Assert.Equal(["first", "later"], sink.Deliveries.Select(static delivery => delivery.Item.Title));
        Assert.False(scheduler.Completion.IsFaulted);
    }

    private sealed class ThrowOnceThenRecordingSink : IReminderSink
    {
        private readonly object _gate = new();
        private readonly List<ScheduledReminder> _deliveries = [];
        private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _changed = NewSignal();
        private bool _throwOnNextDelivery = true;

        public IReadOnlyList<ScheduledReminder> Deliveries
        {
            get
            {
                lock (_gate)
                {
                    return _deliveries.ToArray();
                }
            }
        }

        public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_throwOnNextDelivery)
                {
                    _throwOnNextDelivery = false;
                    _firstAttempt.TrySetResult();
                    throw new InvalidOperationException("The notification service rejected this delivery.");
                }

                _deliveries.Add(reminder);
                _changed.TrySetResult();
                _changed = NewSignal();
                return Task.CompletedTask;
            }
        }

        public Task DeliverMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            Task.CompletedTask;

        public Task WaitForFirstAttemptAsync() => _firstAttempt.Task;

        public async Task WaitForCountAsync(int count)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_deliveries.Count >= count)
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed;
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingScheduledQueryRepository :
        FakeReminderRepository
    {
        private int _queries;
        public int QueryCount => Volatile.Read(ref _queries);
        public TaskCompletionSource FirstQueryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondQueryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstQuery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<ScheduledReminder>>
            GetScheduledAsync(CancellationToken ct)
        {
            var query = Interlocked.Increment(ref _queries);
            if (query == 1)
            {
                FirstQueryEntered.TrySetResult();
                await ReleaseFirstQuery.Task;
            }
            else if (query == 2)
            {
                SecondQueryEntered.TrySetResult();
            }
            return await base.GetScheduledAsync(ct);
        }
    }

    private sealed class ObservingReminderRepository : FakeReminderRepository
    {
        private readonly object _signalGate = new();
        private TaskCompletionSource _recoverableQueryChanged = NewSignal();
        private readonly TaskCompletionSource _missedTransition = NewSignal();
        private int _recoverableQueries;
        private int _missedTransitionCommitted;

        public bool MissedTransitionCommitted =>
            Volatile.Read(ref _missedTransitionCommitted) != 0;

        public override async Task<IReadOnlyList<ScheduledReminder>> GetRecoverableAsync(
            DateTimeOffset through, CancellationToken ct)
        {
            var reminders = await base.GetRecoverableAsync(through, ct);
            lock (_signalGate)
            {
                Interlocked.Increment(ref _recoverableQueries);
                _recoverableQueryChanged.TrySetResult();
                _recoverableQueryChanged = NewSignal();
            }

            return reminders;
        }

        public override async Task<bool> TryTransitionAsync(
            Guid occurrenceId,
            OccurrenceState expected,
            OccurrenceState next,
            DateTimeOffset handledAt,
            CancellationToken ct)
        {
            var committed = await base.TryTransitionAsync(
                occurrenceId, expected, next, handledAt, ct);
            if (committed && next == OccurrenceState.Missed)
            {
                Interlocked.Exchange(ref _missedTransitionCommitted, 1);
                _missedTransition.TrySetResult();
            }

            return committed;
        }

        public async Task WaitForRecoverableQueryCountAsync(int count)
        {
            while (true)
            {
                Task changed;
                lock (_signalGate)
                {
                    if (Volatile.Read(ref _recoverableQueries) >= count)
                    {
                        return;
                    }

                    changed = _recoverableQueryChanged.Task;
                }

                await changed.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task WaitForMissedTransitionAsync() =>
            _missedTransition.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
