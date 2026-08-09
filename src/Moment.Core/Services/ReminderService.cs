using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.Core.Services;

public interface IReminderService
{
    Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct);
    Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct);
    Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct);
}

public sealed class ReminderService(
    IReminderRepository repository,
    ISchedulerSignal schedulerSignal,
    IClock clock) : IReminderService
{
    public async Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct)
    {
        var createdAt = clock.Now;
        ValidateDraft(draft, createdAt);

        var item = ReminderItem.Create(
            draft.Title, draft.Kind, draft.Importance, createdAt, draft.DueAt, draft.Recurrence);
        var occurrence = ReminderOccurrence.Schedule(item.Id, draft.DueAt);

        await repository.SaveItemWithOccurrenceAsync(item, occurrence, ct);
        schedulerSignal.Refresh();
        return occurrence;
    }

    public async Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct)
    {
        var createdAt = clock.Now;
        ValidateDraft(draft, createdAt);
        ValidateScope(scope);

        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current is null || current.Occurrence.State is
            OccurrenceState.Completed or
            OccurrenceState.Ignored or
            OccurrenceState.Delivering)
        {
            return;
        }

        var item = ReminderItem.Create(
            draft.Title, draft.Kind, draft.Importance, createdAt, draft.DueAt, draft.Recurrence);
        var occurrence = current.Occurrence with
        {
            ItemId = item.Id,
            DueAt = draft.DueAt,
            State = OccurrenceState.Scheduled,
            HandledAt = null,
            DeliveryAttempts = 0,
            LastDeliveryError = null,
            NextDeliveryAttemptAt = null
        };

        await repository.EditAsync(occurrenceId, item, occurrence, scope, ct);
        schedulerSignal.Refresh();
    }

    public async Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
    {
        ValidateScope(scope);

        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current is null || current.Occurrence.State == OccurrenceState.Delivering)
        {
            return;
        }

        await repository.DeleteAsync(occurrenceId, scope, clock.Now, ct);
        schedulerSignal.Refresh();
    }

    private static void ValidateScope(SeriesScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }

    private static void ValidateDraft(ReminderDraft draft, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var title = draft.Title?.Trim();
        if (title is null || title.Length is 0 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        if (draft.DueAt < createdAt
            || !Enum.IsDefined(draft.Kind)
            || !Enum.IsDefined(draft.Importance))
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        if (draft.Recurrence is not null)
        {
            ValidateRecurrence(draft.Recurrence);
        }
    }

    private static void ValidateRecurrence(RecurrenceRule recurrence)
    {
        if (!Enum.IsDefined(recurrence.Kind) || recurrence.DaysOfWeek is null
            || recurrence.DaysOfWeek.Any(day => !Enum.IsDefined(day)))
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence));
        }

        var isValid = recurrence.Kind switch
        {
            RecurrenceKind.Daily or RecurrenceKind.Weekdays => recurrence.DaysOfWeek.Count == 0,
            RecurrenceKind.Weekly => recurrence.DaysOfWeek.Count > 0,
            _ => false
        };

        if (!isValid)
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence));
        }
    }
}
