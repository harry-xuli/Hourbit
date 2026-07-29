using System.Globalization;
using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.TestSupport;

public static class TestData
{
    public static ReminderDraft Draft(string title, string dueAt) =>
        new(title, DateTimeOffset.Parse(dueAt, CultureInfo.InvariantCulture),
            ReminderKind.Countdown, ReminderImportance.Normal, null);

    public static ScheduledReminder Scheduled(
        string title,
        string dueAt,
        ReminderImportance importance = ReminderImportance.Normal)
    {
        var due = DateTimeOffset.Parse(dueAt, CultureInfo.InvariantCulture);
        var item = ReminderItem.Create(title, ReminderKind.Countdown, importance, due, due);
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);

        return new(item, occurrence);
    }
}
