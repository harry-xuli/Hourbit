using Moment.Core.Domain;

namespace Moment.Core.Scheduling;

public sealed record RecoveryResult(
    IReadOnlyList<ScheduledReminder> Immediate,
    IReadOnlyList<ScheduledReminder> Summary);

public sealed class RecoveryClassifier
{
    public RecoveryResult Classify(IReadOnlyList<ScheduledReminder> due, DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-5);
        var immediate = due.Where(reminder =>
            reminder.Item.Importance == ReminderImportance.Important || reminder.Occurrence.DueAt >= cutoff).ToArray();
        var summary = due.Where(reminder =>
            reminder.Item.Importance == ReminderImportance.Normal && reminder.Occurrence.DueAt < cutoff).ToArray();
        return new RecoveryResult(immediate, summary);
    }
}
