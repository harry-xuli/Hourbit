using Moment.Core.Domain;
using Moment.Core.Recurrence;

namespace Moment.Core.Tests.Recurrence;

public sealed class RecurrenceCalculatorTests
{
    [Theory]
    [InlineData("2026-07-31T18:00:00+08:00", "2026-08-03T18:00:00+08:00")]
    [InlineData("2026-08-03T18:00:00+08:00", "2026-08-04T18:00:00+08:00")]
    public void Weekdays_skip_weekends(string afterText, string expectedText)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var rule = RecurrenceRule.Weekdays(new TimeOnly(18, 0));

        var next = new RecurrenceCalculator().NextAfter(
            rule, DateTimeOffset.Parse(afterText), zone);

        Assert.Equal(DateTimeOffset.Parse(expectedText), next);
    }

    [Fact]
    public void Weekly_supports_more_than_one_day()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var rule = RecurrenceRule.Weekly(
            [DayOfWeek.Monday, DayOfWeek.Friday], new TimeOnly(16, 0));

        var next = new RecurrenceCalculator().NextAfter(
            rule, DateTimeOffset.Parse("2026-07-27T16:00:00+08:00"), zone);

        Assert.Equal(DateTimeOffset.Parse("2026-07-31T16:00:00+08:00"), next);
    }

    [Fact]
    public void Daily_crosses_the_year_boundary()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var rule = RecurrenceRule.Daily(new TimeOnly(9, 0));

        var next = new RecurrenceCalculator().NextAfter(
            rule, DateTimeOffset.Parse("2026-12-31T09:00:00+08:00"), zone);

        Assert.Equal(DateTimeOffset.Parse("2027-01-01T09:00:00+08:00"), next);
    }

    [Fact]
    public void Daily_moves_an_invalid_dst_local_time_to_the_first_valid_minute()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var rule = RecurrenceRule.Daily(new TimeOnly(2, 30));

        var next = new RecurrenceCalculator().NextAfter(
            rule, DateTimeOffset.Parse("2026-03-08T01:00:00-05:00"), zone);

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T03:00:00-04:00"), next);
    }

    [Fact]
    public void Daily_chooses_the_earlier_utc_instant_for_an_ambiguous_dst_local_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var rule = RecurrenceRule.Daily(new TimeOnly(1, 30));

        var next = new RecurrenceCalculator().NextAfter(
            rule, DateTimeOffset.Parse("2026-11-01T00:30:00-04:00"), zone);

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:30:00-04:00"), next);
    }
}
