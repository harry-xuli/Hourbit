using Moment.Core.Domain;

namespace Moment.Core.Abstractions;

public interface IReminderRepository
{
    Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct);
    Task<IReadOnlyList<ScheduledReminder>> GetScheduledAsync(CancellationToken ct);
    Task<IReadOnlyList<ScheduledReminder>> GetDueAsync(DateTimeOffset through, CancellationToken ct);
    Task<ScheduledReminder?> GetScheduledReminderAsync(Guid occurrenceId, CancellationToken ct);
    Task<ReminderItem?> GetItemAsync(Guid itemId, CancellationToken ct);
    Task SetOccurrenceStateAsync(Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct);
    Task SaveOccurrenceAsync(ReminderOccurrence occurrence, CancellationToken ct);
    Task<bool> TryMarkFiredAsync(Guid occurrenceId, DateTimeOffset firedAt, CancellationToken ct);
    Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state,
        DateTimeOffset handledAt, ReminderOccurrence? nextOccurrence, CancellationToken ct);
    Task EditAsync(Guid occurrenceId, ReminderItem item,
        ReminderOccurrence occurrence, SeriesScope scope, CancellationToken ct);
    Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct);
}
