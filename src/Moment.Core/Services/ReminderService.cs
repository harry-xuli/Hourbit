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
        ArgumentNullException.ThrowIfNull(draft);

        var item = ReminderItem.Create(
            draft.Title, draft.Kind, draft.Importance, clock.Now, draft.DueAt, draft.Recurrence);
        var occurrence = ReminderOccurrence.Schedule(item.Id, draft.DueAt);

        await repository.SaveItemWithOccurrenceAsync(item, occurrence, ct);
        schedulerSignal.Refresh();
        return occurrence;
    }

    public async Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateScope(scope);

        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current?.Occurrence.State != OccurrenceState.Scheduled)
        {
            return;
        }

        var item = ReminderItem.Create(
            draft.Title, draft.Kind, draft.Importance, clock.Now, draft.DueAt, draft.Recurrence);
        var occurrence = current.Occurrence with
        {
            ItemId = item.Id,
            DueAt = draft.DueAt,
            State = OccurrenceState.Scheduled,
            HandledAt = null
        };

        await repository.EditAsync(occurrenceId, item, occurrence, scope, ct);
        schedulerSignal.Refresh();
    }

    public async Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
    {
        ValidateScope(scope);

        var current = await repository.GetScheduledReminderAsync(occurrenceId, ct);
        if (current?.Occurrence.State != OccurrenceState.Scheduled)
        {
            return;
        }

        await repository.DeleteAsync(occurrenceId, scope, ct);
        schedulerSignal.Refresh();
    }

    private static void ValidateScope(SeriesScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }
}
