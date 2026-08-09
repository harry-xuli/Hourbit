using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Recurrence;

namespace Moment.Core.Scheduling;

public sealed record SchedulerDeliveryFailure(ScheduledReminder Reminder, Exception Exception);

public sealed class ReminderScheduler : ISchedulerSignal, IDisposable
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(5);

    private readonly IReminderRepository _repository;
    private readonly IReminderSink _sink;
    private readonly IClock _clock;
    private readonly IRecurrenceCalculator _recurrence;
    private readonly TimeZoneInfo _zone;
    private readonly ReminderDeliveryPolicy _deliveryPolicy = new();
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _loop;
    private bool _disposed;

    public event Action<SchedulerDeliveryFailure>? DeliveryFailed;
    public event EventHandler? StateChanged;

    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _loop ?? Task.CompletedTask;
            }
        }
    }

    public ReminderScheduler(
        IReminderRepository repository,
        IReminderSink sink,
        IClock clock,
        IRecurrenceCalculator? recurrence = null,
        TimeZoneInfo? zone = null)
    {
        _repository = repository;
        _sink = sink;
        _clock = clock;
        _recurrence = recurrence ?? new RecurrenceCalculator();
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task? completedLoop = null;
            CancellationTokenSource? completedCancellation = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_loop is { IsCompleted: false })
                    return;
                if (_loop is not null)
                {
                    completedLoop = _loop;
                    completedCancellation = _runCancellation;
                    _loop = null;
                    _runCancellation = null;
                }
            }

            if (completedLoop is not null)
            {
                try
                {
                    await completedLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    completedCancellation?.IsCancellationRequested == true)
                {
                }
                finally
                {
                    completedCancellation?.Dispose();
                }
            }

            ct.ThrowIfCancellationRequested();
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellation.Token, ct);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _runCancellation = runCancellation;
                _loop = Task.Run(
                    () => RunAsync(runCancellation.Token),
                    CancellationToken.None);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task? loop;
            CancellationTokenSource? runCancellation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                loop = _loop;
                runCancellation = _runCancellation;
            }

            if (loop is null)
                return;

            runCancellation!.Cancel();
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                runCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loop, loop))
                    {
                        _loop = null;
                        _runCancellation = null;
                    }
                }
                runCancellation.Dispose();
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _refreshSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // The queued signal already guarantees a re-query.
            }
        }
    }

    public void Dispose()
    {
        _transitionGate.Wait();
        try
        {
            Task? loop;
            CancellationTokenSource? runCancellation;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                loop = _loop;
                runCancellation = _runCancellation;
            }

            _disposeCancellation.Cancel();
            runCancellation?.Cancel();
            try
            {
                loop?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            runCancellation?.Dispose();
            _disposeCancellation.Dispose();
            _refreshSignal.Dispose();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var scheduled = await _repository.GetScheduledAsync(ct).ConfigureAwait(false);
                var now = _clock.Now;
                var recoverable = await _repository
                        .GetRecoverableAsync(now, ct)
                        .ConfigureAwait(false);
                var fired = recoverable
                    .Where(static reminder =>
                        reminder.Occurrence.State == OccurrenceState.Fired
                        && reminder.Item.Importance == ReminderImportance.Normal)
                    .ToArray();
                var failedDeliveries = recoverable
                    .Where(static reminder =>
                        reminder.Occurrence.State == OccurrenceState.DeliveryFailed)
                    .ToArray();
                var retryable = failedDeliveries
                    .Where(reminder =>
                        reminder.Occurrence.State == OccurrenceState.DeliveryFailed &&
                        reminder.Occurrence.NextDeliveryAttemptAt <= now)
                    .ToArray();

                foreach (var reminder in retryable)
                {
                    if (await _repository.RetryDeliveryAsync(
                            reminder.Occurrence.Id, now, ct).ConfigureAwait(false))
                        ReportStateChanged();
                }
                if (retryable.Length > 0)
                    continue;

                var due = scheduled.Any(reminder => reminder.Occurrence.DueAt <= now);
                var expired = fired
                    .Where(reminder => now - GetFiredAt(reminder) > GracePeriod)
                    .OrderBy(static reminder => reminder.Occurrence.DueAt)
                    .ThenBy(static reminder => reminder.Occurrence.Id)
                    .ToArray();
                if (due)
                {
                    await FireDueAsync(now, ct).ConfigureAwait(false);
                }

                foreach (var reminder in expired)
                {
                    if (await _repository.TryTransitionAsync(
                            reminder.Occurrence.Id,
                            OccurrenceState.Fired,
                            OccurrenceState.Missed,
                            now,
                            ct)
                        .ConfigureAwait(false))
                    {
                        ReportStateChanged();
                    }
                }

                if (due || expired.Length > 0)
                {
                    continue;
                }

                var nextScheduledAt = scheduled
                    .Select(static reminder => (DateTimeOffset?)reminder.Occurrence.DueAt)
                    .Min();
                var nextFiredAt = fired
                    .Select(reminder => (DateTimeOffset?)GetNextFiredWakeAt(reminder, now))
                    .Min();
                var nextRetryAt = failedDeliveries
                    .Select(static reminder => reminder.Occurrence.NextDeliveryAttemptAt)
                    .Min();
                var nextAt = Min(Min(nextScheduledAt, nextFiredAt), nextRetryAt);
                if (nextAt is null)
                {
                    await _refreshSignal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = _clock.DelayUntilAsync(nextAt.Value, waitCancellation.Token);
                var refresh = _refreshSignal.WaitAsync(waitCancellation.Token);
                var completed = await Task.WhenAny(delay, refresh).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                waitCancellation.Cancel();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task FireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await _repository.GetDueAsync(now, ct).ConfigureAwait(false);
        foreach (var reminder in due
                     .OrderBy(static reminder => reminder.Occurrence.DueAt)
                     .ThenBy(static reminder => reminder.Occurrence.Id))
        {
            if (!await _repository.TryBeginDeliveryAsync(
                    reminder.Occurrence.Id, now, ct).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await _sink.DeliverAsync(reminder, ct).ConfigureAwait(false);
                ReminderOccurrence? next = null;
                if (reminder.Item.Recurrence is { } rule)
                {
                    next = ReminderOccurrence.Schedule(
                        reminder.Item.Id,
                        _recurrence.NextAfter(
                            rule, reminder.Occurrence.DueAt, _zone));
                }
                await _repository.CompleteDeliveryAsync(
                    reminder.Occurrence.Id, now, next, ct).ConfigureAwait(false);
                ReportStateChanged();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var current = await _repository.GetScheduledReminderAsync(
                    reminder.Occurrence.Id, ct).ConfigureAwait(false);
                var attempts = current?.Occurrence.DeliveryAttempts ?? 1;
                var retryAt = _deliveryPolicy.GetNextRetryAt(attempts, now);
                await _repository.RecordDeliveryFailureAsync(
                    reminder.Occurrence.Id,
                    now,
                    exception.GetType().Name,
                    retryAt,
                    ct).ConfigureAwait(false);
                ReportStateChanged();
                ReportDeliveryFailure(new SchedulerDeliveryFailure(reminder, exception));
            }
        }
    }

    private static DateTimeOffset GetFiredAt(ScheduledReminder reminder) =>
        reminder.Occurrence.HandledAt ?? reminder.Occurrence.DueAt;

    private static DateTimeOffset GetNextFiredWakeAt(
        ScheduledReminder reminder, DateTimeOffset now)
    {
        var deadline = GetFiredAt(reminder).Add(GracePeriod);
        return deadline == now ? deadline.AddTicks(1) : deadline;
    }

    private static DateTimeOffset? Min(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null || first <= second ? first : second;
    }

    private void ReportDeliveryFailure(SchedulerDeliveryFailure failure)
    {
        var handlers = DeliveryFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<SchedulerDeliveryFailure> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(failure);
            }
            catch
            {
                // Observers must not be allowed to stop the scheduling loop.
            }
        }
    }

    private void ReportStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Observers must not be allowed to stop the scheduling loop.
            }
        }
    }
}
