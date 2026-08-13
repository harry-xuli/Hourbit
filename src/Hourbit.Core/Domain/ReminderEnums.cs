namespace Hourbit.Core.Domain;

public enum ReminderKind { Countdown, Alarm, Plan }

public enum ReminderImportance { Normal, Important }

public enum OccurrenceState
{
    Scheduled,
    Fired,
    Completed,
    Ignored,
    Missed,
    Snoozed,
    Delivering,
    DeliveryFailed
}

public enum RecurrenceKind { Daily, Weekdays, Weekly }

public enum SeriesScope { OccurrenceOnly, ThisAndFuture }
