namespace Hourbit.Core.Domain;

public sealed record ReminderOccurrence(
    Guid Id,
    Guid ItemId,
    DateTimeOffset DueAt,
    OccurrenceState State,
    DateTimeOffset? HandledAt,
    Guid? SnoozeParentId,
    int DeliveryAttempts = 0,
    string? LastDeliveryError = null,
    DateTimeOffset? NextDeliveryAttemptAt = null)
{
    public static ReminderOccurrence Schedule(
        Guid itemId,
        DateTimeOffset dueAt,
        Guid? snoozeParentId = null) =>
        new(Guid.NewGuid(), itemId, dueAt, OccurrenceState.Scheduled, null, snoozeParentId);
}
