using Hourbit.Core.Domain;

namespace Hourbit.Core.Recurrence;

public interface IRecurrenceCalculator
{
    DateTimeOffset NextAfter(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo zone);
}

public sealed class RecurrenceCalculator : IRecurrenceCalculator
{
    public DateTimeOffset NextAfter(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(zone);

        var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;

        for (var offset = 0; offset <= 370; offset++)
        {
            var date = localAfter.Date.AddDays(offset);
            if (!Allows(rule, date.DayOfWeek))
            {
                continue;
            }

            var candidateLocal = date + rule.Time.ToTimeSpan();
            if (candidateLocal <= localAfter)
            {
                continue;
            }

            var candidate = ResolveLocal(candidateLocal, zone);
            if (candidate <= after)
            {
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException("No occurrence found within 370 days.");
    }

    private static bool Allows(RecurrenceRule rule, DayOfWeek dayOfWeek) =>
        rule.Kind switch
        {
            RecurrenceKind.Daily => true,
            RecurrenceKind.Weekdays => dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            RecurrenceKind.Weekly => rule.DaysOfWeek.Contains(dayOfWeek),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Kind, "Unknown recurrence kind.")
        };

    private static DateTimeOffset ResolveLocal(DateTime localTime, TimeZoneInfo zone)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        while (zone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(localTime))
        {
            return zone.GetAmbiguousTimeOffsets(localTime)
                .Select(offset => new DateTimeOffset(localTime, offset))
                .MinBy(candidate => candidate.UtcDateTime);
        }

        return new DateTimeOffset(localTime, zone.GetUtcOffset(localTime));
    }
}
