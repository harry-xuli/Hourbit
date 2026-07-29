namespace Moment.Core.Domain;

public sealed record ReminderItem(
    Guid Id,
    string Title,
    ReminderKind Kind,
    ReminderImportance Importance,
    DateTimeOffset CreatedAt,
    RecurrenceRule? Recurrence)
{
    public static ReminderItem Create(
        string title,
        ReminderKind kind,
        ReminderImportance importance,
        DateTimeOffset createdAt,
        DateTimeOffset firstDueAt,
        RecurrenceRule? recurrence = null)
    {
        var normalized = title.Trim();
        if (normalized.Length is 0 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(title));
        }

        if (firstDueAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDueAt));
        }

        return new(Guid.NewGuid(), normalized, kind, importance, createdAt, recurrence);
    }
}
