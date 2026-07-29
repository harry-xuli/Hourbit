using Moment.Core.Abstractions;
using Moment.Core.Domain;

namespace Moment.TestSupport;

public class FakeReminderRepository : IReminderRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ReminderItem> _items = [];
    private readonly Dictionary<Guid, ReminderOccurrence> _occurrences = [];

    public virtual Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            _items[item.Id] = item;
            _occurrences[occurrence.Id] = occurrence;
        }

        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<ScheduledReminder>> GetScheduledAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ScheduledReminder>>(GetMatching(static occurrence => occurrence.State == OccurrenceState.Scheduled));
        }
    }

    public virtual Task<IReadOnlyList<ScheduledReminder>> GetDueAsync(DateTimeOffset through, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ScheduledReminder>>(GetMatching(occurrence =>
                occurrence.State == OccurrenceState.Scheduled && occurrence.DueAt <= through));
        }
    }

    public virtual Task<ScheduledReminder?> GetScheduledReminderAsync(Guid occurrenceId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(CreateScheduledReminder(occurrenceId));
        }
    }

    public virtual Task<ReminderItem?> GetItemAsync(Guid itemId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault(itemId));
        }
    }

    public virtual Task SetOccurrenceStateAsync(Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                _occurrences[occurrenceId] = occurrence with { State = state, HandledAt = handledAt };
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task SaveOccurrenceAsync(ReminderOccurrence occurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            _occurrences[occurrence.Id] = occurrence;
        }

        return Task.CompletedTask;
    }

    public virtual Task<bool> TryMarkFiredAsync(Guid occurrenceId, DateTimeOffset firedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) || occurrence.State != OccurrenceState.Scheduled)
            {
                return Task.FromResult(false);
            }

            _occurrences[occurrenceId] = occurrence with { State = OccurrenceState.Fired, HandledAt = firedAt };
            return Task.FromResult(true);
        }
    }

    public virtual Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state,
        DateTimeOffset handledAt, ReminderOccurrence? nextOccurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                _occurrences[occurrenceId] = occurrence with { State = state, HandledAt = handledAt };
            }

            if (nextOccurrence is not null)
            {
                _occurrences[nextOccurrence.Id] = nextOccurrence;
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task EditAsync(Guid occurrenceId, ReminderItem item,
        ReminderOccurrence occurrence, SeriesScope scope, CancellationToken ct)
    {
        lock (_gate)
        {
            if (scope == SeriesScope.ThisAndFuture)
            {
                _items[item.Id] = item;
            }

            _occurrences.Remove(occurrenceId);
            _occurrences[occurrence.Id] = occurrence;
        }

        return Task.CompletedTask;
    }

    public virtual Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                return Task.CompletedTask;
            }

            if (scope == SeriesScope.OccurrenceOnly)
            {
                _occurrences.Remove(occurrenceId);
                return Task.CompletedTask;
            }

            if (_items.TryGetValue(occurrence.ItemId, out var item))
            {
                _items[occurrence.ItemId] = item with { Recurrence = null };
            }

            foreach (var id in _occurrences
                         .Where(pair => pair.Value.ItemId == occurrence.ItemId
                             && pair.Value.State == OccurrenceState.Scheduled
                             && pair.Value.DueAt >= occurrence.DueAt)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _occurrences.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<ScheduledReminder> GetMatching(Func<ReminderOccurrence, bool> predicate) =>
        _occurrences.Values
            .Where(predicate)
            .OrderBy(static occurrence => occurrence.DueAt)
            .ThenBy(static occurrence => occurrence.Id)
            .Select(occurrence => new ScheduledReminder(_items[occurrence.ItemId], occurrence))
            .ToArray();

    private ScheduledReminder? CreateScheduledReminder(Guid occurrenceId) =>
        _occurrences.TryGetValue(occurrenceId, out var occurrence) && _items.TryGetValue(occurrence.ItemId, out var item)
            ? new ScheduledReminder(item, occurrence)
            : null;
}
