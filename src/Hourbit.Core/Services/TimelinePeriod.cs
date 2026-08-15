using System.Globalization;
using Hourbit.Core.Analytics;

namespace Hourbit.Core.Services;

public enum TimelinePeriodKind
{
    Day,
    Week,
    Month
}

public sealed record TimelinePeriod(LocalDateRange Range, string Label)
{
    public static TimelinePeriod Create(
        DateOnly selectedDate,
        TimelinePeriodKind kind,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var range = kind switch
        {
            TimelinePeriodKind.Day => new LocalDateRange(selectedDate, selectedDate),
            TimelinePeriodKind.Week => CreateWeekRange(
                selectedDate, DayOfWeek.Monday),
            TimelinePeriodKind.Month => new LocalDateRange(
                new DateOnly(selectedDate.Year, selectedDate.Month, 1),
                new DateOnly(
                    selectedDate.Year,
                    selectedDate.Month,
                    DateTime.DaysInMonth(selectedDate.Year, selectedDate.Month))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new TimelinePeriod(range, FormatLabel(range, kind, culture));
    }

    private static LocalDateRange CreateWeekRange(
        DateOnly selectedDate,
        DayOfWeek firstDayOfWeek)
    {
        var daysSinceStart =
            ((int)selectedDate.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = selectedDate.AddDays(-daysSinceStart);
        return new LocalDateRange(start, start.AddDays(6));
    }

    private static string FormatLabel(
        LocalDateRange range,
        TimelinePeriodKind kind,
        CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName.Equals(
                "zh", StringComparison.OrdinalIgnoreCase))
        {
            return kind switch
            {
                TimelinePeriodKind.Day => range.Start.ToString(
                    "yyyy年M月d日 dddd", culture),
                TimelinePeriodKind.Week =>
                    $"{range.Start.ToString("yyyy年M月d日", culture)} – " +
                    range.End.ToString("yyyy年M月d日", culture),
                TimelinePeriodKind.Month => range.Start.ToString("yyyy年M月", culture),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        return kind switch
        {
            TimelinePeriodKind.Day => range.Start.ToString("D", culture),
            TimelinePeriodKind.Week => FormatEnglishWeek(range, culture),
            TimelinePeriodKind.Month => range.Start.ToString("Y", culture),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string FormatEnglishWeek(LocalDateRange range, CultureInfo culture)
    {
        var start = range.Start.Year == range.End.Year
            ? range.Start.ToString("MMMM d", culture)
            : range.Start.ToString("MMMM d, yyyy", culture);
        return $"{start} – {range.End.ToString("MMMM d, yyyy", culture)}";
    }
}
