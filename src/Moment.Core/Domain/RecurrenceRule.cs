using System.Collections.Immutable;

namespace Moment.Core.Domain;

public sealed record RecurrenceRule(
    RecurrenceKind Kind,
    ImmutableHashSet<DayOfWeek> DaysOfWeek,
    TimeOnly Time)
{
    public static RecurrenceRule Daily(TimeOnly time) =>
        new(RecurrenceKind.Daily, ImmutableHashSet<DayOfWeek>.Empty, time);

    public static RecurrenceRule Weekdays(TimeOnly time) =>
        new(RecurrenceKind.Weekdays, ImmutableHashSet<DayOfWeek>.Empty, time);

    public static RecurrenceRule Weekly(IEnumerable<DayOfWeek> daysOfWeek, TimeOnly time)
    {
        ArgumentNullException.ThrowIfNull(daysOfWeek);

        var normalized = daysOfWeek.ToImmutableHashSet();
        if (normalized.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(daysOfWeek));
        }

        return new(RecurrenceKind.Weekly, normalized, time);
    }
}
