namespace Moment.Core.Domain;

public sealed record ReminderOccurrence(
    Guid Id,
    Guid ItemId,
    DateTimeOffset DueAt,
    OccurrenceState State,
    DateTimeOffset? HandledAt,
    Guid? SnoozeParentId)
{
    public static ReminderOccurrence Schedule(
        Guid itemId,
        DateTimeOffset dueAt,
        Guid? snoozeParentId = null) =>
        new(Guid.NewGuid(), itemId, dueAt, OccurrenceState.Scheduled, null, snoozeParentId);
}
