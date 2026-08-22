using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Recurrence;

namespace Hourbit.Core.Services;

public interface IReminderActionService
{
    Task CompleteAsync(Guid occurrenceId, CancellationToken ct);
    Task IgnoreAsync(Guid occurrenceId, CancellationToken ct);
    Task<ReminderOccurrence> SnoozeAsync(Guid occurrenceId, TimeSpan delay, CancellationToken ct);
}

public sealed class ReminderActionService(
    IReminderRepository repository,
    IRecurrenceCalculator recurrenceCalculator,
    ISchedulerSignal schedulerSignal,
    IClock clock,
    TimeZoneInfo schedulingTimeZone) : IReminderActionService
{
    private readonly TimeZoneInfo _schedulingTimeZone = schedulingTimeZone ??
        throw new ArgumentNullException(nameof(schedulingTimeZone));

    public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) =>
        ApplyActionAsync(occurrenceId, OccurrenceState.Completed, ct);

    public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) =>
        ApplyActionAsync(occurrenceId, OccurrenceState.Ignored, ct);

    public async Task<ReminderOccurrence> SnoozeAsync(Guid occurrenceId, TimeSpan delay, CancellationToken ct)
    {
        var current = await GetActionableReminderAsync(occurrenceId, ct);
        ValidateSnoozeDelay(current.Item.Importance, delay);

        var handledAt = clock.Now;
        var snoozed = ReminderOccurrence.Schedule(current.Item.Id, handledAt.Add(delay), current.Occurrence.Id);
        await repository.ApplyActionAsync(occurrenceId, OccurrenceState.Snoozed, handledAt, snoozed, ct);
        schedulerSignal.Refresh();
        return snoozed;
    }

    private async Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state, CancellationToken ct)
    {
        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current is null || !IsActionable(current.Occurrence.State))
        {
            return;
        }

        // A delivered (Fired) recurrence already scheduled its next occurrence during
        // delivery. Only a still-Scheduled occurrence needs a continuation here,
        // otherwise the same due_at_utc would be inserted twice.
        var next = current.Item.Recurrence is not null &&
                   current.Occurrence.State == OccurrenceState.Scheduled
            ? ReminderOccurrence.Schedule(current.Item.Id, recurrenceCalculator.NextAfter(
                current.Item.Recurrence, current.Occurrence.DueAt, _schedulingTimeZone))
            : null;

        await repository.ApplyActionAsync(occurrenceId, state, clock.Now, next, ct);
        schedulerSignal.Refresh();
    }

    private async Task<ScheduledReminder> GetActionableReminderAsync(Guid occurrenceId, CancellationToken ct)
    {
        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current is null || !IsActionable(current.Occurrence.State))
        {
            throw new InvalidOperationException("Reminder occurrence is not actionable.");
        }

        return current;
    }

    private static bool IsActionable(OccurrenceState state) =>
        state is OccurrenceState.Scheduled or OccurrenceState.Fired or OccurrenceState.Missed;

    private static void ValidateSnoozeDelay(ReminderImportance importance, TimeSpan delay)
    {
        var allowed = importance == ReminderImportance.Important
            ? delay == TimeSpan.FromMinutes(5)
              || delay == TimeSpan.FromMinutes(10)
              || delay == TimeSpan.FromMinutes(30)
              || delay == TimeSpan.FromMinutes(60)
            : delay == TimeSpan.FromMinutes(10);

        if (!allowed)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }
    }
}
