namespace Hourbit.Core.Domain;

public sealed record ScheduledReminder(ReminderItem Item, ReminderOccurrence Occurrence);
