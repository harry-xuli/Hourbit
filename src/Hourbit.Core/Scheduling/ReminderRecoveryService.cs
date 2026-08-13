using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Recurrence;

namespace Hourbit.Core.Scheduling;

public sealed record ReminderRecoveryResult(int Fired, int Missed, int Failed);

public sealed class ReminderRecoveryService
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(5);

    private readonly IReminderRepository _repository;
    private readonly IReminderSink _reminderSink;
    private readonly IReminderRecoverySummarySink _summarySink;
    private readonly IRecurrenceCalculator _recurrence;
    private readonly TimeZoneInfo _zone;
    private readonly ReminderDeliveryPolicy _deliveryPolicy = new();
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    public event Action<Exception>? RecoveryFailed;

    public ReminderRecoveryService(
        IReminderRepository repository,
        IReminderSink reminderSink,
        IReminderRecoverySummarySink summarySink,
        IRecurrenceCalculator? recurrence = null,
        TimeZoneInfo? zone = null)
    {
        _repository = repository;
        _reminderSink = reminderSink;
        _summarySink = summarySink;
        _recurrence = recurrence ?? new RecurrenceCalculator();
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public async Task<ReminderRecoveryResult> RecoverAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        await _recoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fired = 0;
            var missed = 0;
            var failed = 0;
            var newlyMissed = new List<ScheduledReminder>();
            var recoverable = await _repository
                .GetRecoverableAsync(now, ct)
                .ConfigureAwait(false);

            foreach (var reminder in recoverable
                         .OrderBy(static reminder => reminder.Occurrence.DueAt)
                         .ThenBy(static reminder => reminder.Occurrence.Id))
            {
                ct.ThrowIfCancellationRequested();
                if (reminder.Occurrence.State == OccurrenceState.Delivering)
                {
                    await _repository.RecordDeliveryFailureAsync(
                        reminder.Occurrence.Id, now, "InterruptedDelivery", now, ct)
                        .ConfigureAwait(false);
                    failed++;
                    continue;
                }
                var next = GetRecoveryState(reminder, now);
                if (next is null)
                {
                    continue;
                }

                var claimed = next == OccurrenceState.Fired
                    ? await _repository.TryBeginDeliveryAsync(
                        reminder.Occurrence.Id, now, ct).ConfigureAwait(false)
                    : await _repository.TryTransitionAsync(
                        reminder.Occurrence.Id,
                        reminder.Occurrence.State,
                        next.Value,
                        now,
                        ct).ConfigureAwait(false);
                if (!claimed)
                {
                    continue;
                }

                if (next == OccurrenceState.Missed)
                {
                    missed++;
                    newlyMissed.Add(reminder);
                    continue;
                }

                try
                {
                    await _reminderSink.DeliverAsync(reminder, ct).ConfigureAwait(false);
                    ReminderOccurrence? successor = null;
                    if (reminder.Item.Recurrence is { } rule)
                        successor = ReminderOccurrence.Schedule(
                            reminder.Item.Id,
                            _recurrence.NextAfter(
                                rule, reminder.Occurrence.DueAt, _zone));
                    await _repository.CompleteDeliveryAsync(
                        reminder.Occurrence.Id, now, successor, ct)
                        .ConfigureAwait(false);
                    fired++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var current = await _repository.GetScheduledReminderAsync(
                        reminder.Occurrence.Id, ct).ConfigureAwait(false);
                    var retryAt = _deliveryPolicy.GetNextRetryAt(
                        current?.Occurrence.DeliveryAttempts ?? 1, now);
                    await _repository.RecordDeliveryFailureAsync(
                        reminder.Occurrence.Id, now,
                        exception.GetType().Name, retryAt, ct)
                        .ConfigureAwait(false);
                    failed++;
                    ReportFailure(exception);
                }
            }

            if (newlyMissed.Count > 0)
            {
                try
                {
                    await _summarySink
                        .SendMissedSummaryAsync(newlyMissed, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    ReportFailure(exception);
                }
            }

            return new ReminderRecoveryResult(fired, missed, failed);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private static OccurrenceState? GetRecoveryState(
        ScheduledReminder reminder, DateTimeOffset now)
    {
        var occurrence = reminder.Occurrence;
        if (occurrence.State == OccurrenceState.Scheduled)
        {
            return reminder.Item.Importance == ReminderImportance.Important
                   || now - occurrence.DueAt <= GracePeriod
                ? OccurrenceState.Fired
                : OccurrenceState.Missed;
        }

        if (occurrence.State == OccurrenceState.Fired
            && reminder.Item.Importance == ReminderImportance.Normal
            && now - (occurrence.HandledAt ?? occurrence.DueAt) > GracePeriod)
        {
            return OccurrenceState.Missed;
        }

        return null;
    }

    private void ReportFailure(Exception exception)
    {
        foreach (Action<Exception> observer in
                 RecoveryFailed?.GetInvocationList() ?? [])
        {
            try
            {
                observer(exception);
            }
            catch
            {
                // Faulting observers cannot stop recovery of later reminders.
            }
        }
    }
}
