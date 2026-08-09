using Moment.Core.Abstractions;
using Moment.Core.Domain;

namespace Moment.TestSupport;

public class FakeReminderRepository : IReminderRepository
{
    private readonly object _gate = new();
    public DateTimeOffset? LastDeletedAt { get; private set; }
    private readonly Dictionary<Guid, ReminderItem> _items = [];
    private readonly Dictionary<Guid, ReminderOccurrence> _occurrences = [];

    public Task AddAsync(ScheduledReminder reminder, CancellationToken ct = default) =>
        SaveItemWithOccurrenceAsync(reminder.Item, reminder.Occurrence, ct);

    public virtual Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(item.Id) || occurrence.ItemId != item.Id || _occurrences.ContainsKey(occurrence.Id)
                || _occurrences.Values.Any(existing => existing.ItemId == occurrence.ItemId
                    && existing.DueAt.UtcDateTime == occurrence.DueAt.UtcDateTime))
            {
                throw new InvalidOperationException("Item already exists or does not own the occurrence.");
            }

            _items.Add(item.Id, item);
            _occurrences.Add(occurrence.Id, occurrence);
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

    public virtual Task<IReadOnlyList<ScheduledReminder>> GetRecoverableAsync(DateTimeOffset through, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ScheduledReminder>>(GetMatching(occurrence =>
                (occurrence.State == OccurrenceState.Scheduled && occurrence.DueAt <= through)
                || (occurrence.State == OccurrenceState.Fired
                    && _items[occurrence.ItemId].Importance == ReminderImportance.Normal)
                || (occurrence.State == OccurrenceState.DeliveryFailed
                    && occurrence.NextDeliveryAttemptAt is not null)
                || occurrence.State == OccurrenceState.Delivering));
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
            EnsureOccurrenceCanBeInserted(occurrence);
            _occurrences.Add(occurrence.Id, occurrence);
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

    public virtual Task<bool> TryBeginDeliveryAsync(
        Guid occurrenceId, DateTimeOffset attemptedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) ||
                occurrence.State != OccurrenceState.Scheduled)
                return Task.FromResult(false);
            _occurrences[occurrenceId] = occurrence with
            {
                State = OccurrenceState.Delivering,
                HandledAt = attemptedAt,
                DeliveryAttempts = occurrence.DeliveryAttempts + 1,
                LastDeliveryError = null,
                NextDeliveryAttemptAt = null
            };
            return Task.FromResult(true);
        }
    }

    public virtual Task CompleteDeliveryAsync(
        Guid occurrenceId, DateTimeOffset firedAt,
        ReminderOccurrence? nextOccurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) ||
                occurrence.State != OccurrenceState.Delivering)
                return Task.CompletedTask;
            if (nextOccurrence is not null)
                EnsureOccurrenceCanBeInserted(nextOccurrence);
            _occurrences[occurrenceId] = occurrence with
            {
                State = OccurrenceState.Fired,
                HandledAt = firedAt,
                LastDeliveryError = null,
                NextDeliveryAttemptAt = null
            };
            if (nextOccurrence is not null)
                _occurrences.Add(nextOccurrence.Id, nextOccurrence);
        }
        return Task.CompletedTask;
    }

    public virtual Task RecordDeliveryFailureAsync(
        Guid occurrenceId, DateTimeOffset attemptedAt, string errorCode,
        DateTimeOffset? retryAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_occurrences.TryGetValue(occurrenceId, out var occurrence) &&
                occurrence.State == OccurrenceState.Delivering)
                _occurrences[occurrenceId] = occurrence with
                {
                    State = OccurrenceState.DeliveryFailed,
                    HandledAt = attemptedAt,
                    LastDeliveryError = errorCode,
                    NextDeliveryAttemptAt = retryAt
                };
        }
        return Task.CompletedTask;
    }

    public virtual Task<bool> RetryDeliveryAsync(
        Guid occurrenceId, DateTimeOffset retryAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) ||
                occurrence.State != OccurrenceState.DeliveryFailed ||
                occurrence.NextDeliveryAttemptAt is null ||
                occurrence.NextDeliveryAttemptAt > retryAt)
                return Task.FromResult(false);
            _occurrences[occurrenceId] = occurrence with
            {
                State = OccurrenceState.Scheduled,
                HandledAt = null,
                NextDeliveryAttemptAt = null
            };
            return Task.FromResult(true);
        }
    }

    public virtual Task<bool> TryTransitionAsync(Guid occurrenceId, OccurrenceState expected,
        OccurrenceState next, DateTimeOffset handledAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) || occurrence.State != expected)
            {
                return Task.FromResult(false);
            }

            _occurrences[occurrenceId] = occurrence with { State = next, HandledAt = handledAt };
            return Task.FromResult(true);
        }
    }

    public virtual Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state,
        DateTimeOffset handledAt, ReminderOccurrence? nextOccurrence, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence)
                || occurrence.State is not (OccurrenceState.Scheduled or OccurrenceState.Fired))
            {
                return Task.CompletedTask;
            }

            if (nextOccurrence is not null)
            {
                EnsureOccurrenceCanBeInserted(nextOccurrence);
            }

            _occurrences[occurrenceId] = occurrence with { State = state, HandledAt = handledAt };
            if (nextOccurrence is not null)
            {
                _occurrences.Add(nextOccurrence.Id, nextOccurrence);
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task EditAsync(Guid occurrenceId, ReminderItem item,
        ReminderOccurrence occurrence, SeriesScope scope, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var current))
            {
                return Task.CompletedTask;
            }

            if (scope == SeriesScope.ThisAndFuture)
            {
                if (HasPrimaryKeyConflictAfterFutureDeletion(current, occurrence))
                {
                    throw new InvalidOperationException("Occurrence already exists.");
                }

                var futureItem = item with { Id = Guid.NewGuid() };
                _items.Add(futureItem.Id, futureItem);
                foreach (var id in _occurrences
                             .Where(pair => pair.Value.ItemId == current.ItemId
                                 && pair.Value.State == OccurrenceState.Scheduled
                                 && pair.Value.DueAt >= current.DueAt)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _occurrences.Remove(id);
                }

                _occurrences.Add(occurrence.Id, occurrence with { ItemId = futureItem.Id });
                return Task.CompletedTask;
            }

            if (occurrence.Id != occurrenceId && _occurrences.ContainsKey(occurrence.Id))
            {
                throw new InvalidOperationException("Occurrence already exists.");
            }

            var singleItem = item with { Id = Guid.NewGuid(), Recurrence = null };
            _items.Add(singleItem.Id, singleItem);
            if (occurrence.Id != occurrenceId)
            {
                _occurrences.Remove(occurrenceId);
                _occurrences.Add(occurrence.Id, occurrence with { ItemId = singleItem.Id });
            }
            else
            {
                _occurrences[occurrenceId] = occurrence with { ItemId = singleItem.Id };
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task DeleteAsync(
        Guid occurrenceId,
        SeriesScope scope,
        CancellationToken ct) =>
        DeleteAsync(occurrenceId, scope, DateTimeOffset.UtcNow, ct);

    public virtual Task DeleteAsync(
        Guid occurrenceId,
        SeriesScope scope,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        LastDeletedAt = deletedAt;
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

    private void EnsureOccurrenceCanBeInserted(ReminderOccurrence occurrence)
    {
        if (!_items.ContainsKey(occurrence.ItemId))
        {
            throw new InvalidOperationException("Occurrence item does not exist.");
        }

        if (_occurrences.ContainsKey(occurrence.Id)
            || _occurrences.Values.Any(existing => existing.ItemId == occurrence.ItemId
                && existing.DueAt.UtcDateTime == occurrence.DueAt.UtcDateTime))
        {
            throw new InvalidOperationException("Occurrence already exists.");
        }
    }

    private bool HasPrimaryKeyConflictAfterFutureDeletion(ReminderOccurrence current, ReminderOccurrence replacement)
    {
        if (!_occurrences.TryGetValue(replacement.Id, out var existing))
        {
            return false;
        }

        return existing.ItemId != current.ItemId
            || existing.State != OccurrenceState.Scheduled
            || existing.DueAt < current.DueAt;
    }
}
