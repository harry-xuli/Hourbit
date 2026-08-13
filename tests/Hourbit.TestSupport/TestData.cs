using System.Globalization;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;

namespace Hourbit.TestSupport;

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

    public static ReminderAlert Alert(string title, int dueMinute) =>
        new(Guid.NewGuid(), title, new DateTimeOffset(2026, 8, 1, 9, dueMinute, 0, TimeSpan.FromHours(8)));

    public static TimelineRow Row(
        string title,
        string dueAt,
        OccurrenceState state = OccurrenceState.Scheduled,
        ReminderKind kind = ReminderKind.Plan,
        ReminderImportance importance = ReminderImportance.Normal,
        string? recurrenceText = null) =>
        new(Guid.NewGuid(), title,
            DateTimeOffset.Parse(dueAt, CultureInfo.InvariantCulture),
            kind, importance, state, recurrenceText);
}
