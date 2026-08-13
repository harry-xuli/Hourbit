namespace Hourbit.Core.Domain;

/// <summary>Information needed to present an important reminder without exposing storage details.</summary>
public sealed record ReminderAlert(
    Guid OccurrenceId,
    string Title,
    DateTimeOffset DueAt,
    string? CustomAudioPath = null)
{
    public static ReminderAlert From(ScheduledReminder reminder) => new(
        reminder.Occurrence.Id,
        reminder.Item.Title,
        reminder.Occurrence.DueAt);
}

public enum ImportantAlertAction
{
    Complete,
    Snooze5,
    Snooze10,
    Snooze30,
    Snooze60,
    Ignore,
    Close
}
