using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.Core.Tests.Parsing;

public sealed class ChineseTimeParserTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-29T09:00:00+08:00");

    private static readonly TimeZoneInfo ChinaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    [Theory]
    [InlineData("20分钟后休息", "休息", "2026-07-29T09:20:00+08:00")]
    [InlineData("下午3点半提醒我打电话", "打电话", "2026-07-29T15:30:00+08:00")]
    [InlineData("明早9点开会", "开会", "2026-07-30T09:00:00+08:00")]
    public void Parses_supported_phrases(string text, string title, string due)
    {
        var result = Assert.IsType<ParseResult.Success>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));

        Assert.Equal(title, result.Draft.Title);
        Assert.Equal(DateTimeOffset.Parse(due), result.Draft.DueAt);
        Assert.Equal(ReminderKind.Countdown, result.Draft.Kind);
        Assert.Equal(ReminderImportance.Normal, result.Draft.Importance);
        Assert.Null(result.Draft.Recurrence);
    }

    [Theory]
    [InlineData("晚上提醒我看书", "看书")]
    [InlineData("待会提醒我喝水", "喝水")]
    [InlineData("下周提醒我交报告", "交报告")]
    public void Returns_choices_for_ambiguous_phrases(string text, string title)
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));

        Assert.NotEmpty(result.Choices);
        Assert.All(result.Choices, choice => Assert.Equal(title, choice.Draft.Title));
    }

    [Theory]
    [InlineData("每个工作日18点下班", RecurrenceKind.Weekdays,
        "2026-07-29T18:00:00+08:00")]
    [InlineData("每周五下午4点写周报", RecurrenceKind.Weekly,
        "2026-07-31T16:00:00+08:00")]
    public void Parses_recurrence_rules(string text, RecurrenceKind recurrenceKind, string due)
    {
        var result = Assert.IsType<ParseResult.Success>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));

        var recurrence = Assert.IsType<RecurrenceRule>(result.Draft.Recurrence);
        Assert.Equal(recurrenceKind, recurrence.Kind);
        Assert.Equal(DateTimeOffset.Parse(due), result.Draft.DueAt);
        Assert.Equal(TimeOnly.FromDateTime(DateTimeOffset.Parse(due).DateTime), recurrence.Time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("今天8点开会")]
    public void Rejects_blank_and_past_explicit_input(string text)
    {
        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Fact]
    public void Rejects_a_title_longer_than_200_characters()
    {
        var text = $"20分钟后{new string('测', 201)}";

        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Fact]
    public void Returns_time_preserving_choices_for_an_ambiguous_week()
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse("下周下午4点写周报", Now, ChinaTimeZone));

        Assert.All(result.Choices, choice =>
        {
            Assert.Equal("写周报", choice.Draft.Title);
            Assert.Equal(new TimeOnly(16, 0), TimeOnly.FromDateTime(choice.Draft.DueAt.DateTime));
            Assert.True(choice.Draft.DueAt > Now);
        });
    }

    [Fact]
    public void Does_not_silently_schedule_a_vague_relative_phrase_with_a_clock()
    {
        var earlyNow = DateTimeOffset.Parse("2026-07-29T01:00:00+08:00");
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse("待会3点提醒我喝水", earlyNow, ChinaTimeZone));

        Assert.All(result.Choices, choice =>
        {
            Assert.Equal("喝水", choice.Draft.Title);
            Assert.True(choice.Draft.DueAt > earlyNow);
        });
    }

    [Fact]
    public void Retains_recurrence_in_choices_for_an_ambiguous_recurring_clock()
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse("每周五晚上看书", Now, ChinaTimeZone));

        Assert.All(result.Choices, choice =>
        {
            var recurrence = Assert.IsType<RecurrenceRule>(choice.Draft.Recurrence);
            Assert.Equal(RecurrenceKind.Weekly, recurrence.Kind);
            Assert.Equal([DayOfWeek.Friday], recurrence.DaysOfWeek.Order());
            Assert.Equal(recurrence.Time, TimeOnly.FromDateTime(choice.Draft.DueAt.DateTime));
            Assert.True(choice.Draft.DueAt > Now);
        });
    }

    [Theory]
    [InlineData("晚上提醒我")]
    [InlineData("待会提醒我")]
    [InlineData("下周提醒我")]
    public void Rejects_an_ambiguous_phrase_without_a_title(string text)
    {
        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Fact]
    public void Rejects_an_overlong_title_in_an_ambiguous_phrase()
    {
        var text = $"晚上提醒我{new string('测', 201)}";

        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Fact]
    public void Offers_only_future_candidates_for_an_ambiguous_phrase()
    {
        var lateNow = DateTimeOffset.Parse("2026-07-29T22:00:00+08:00");
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse("晚上提醒我看书", lateNow, ChinaTimeZone));

        Assert.All(result.Choices, choice => Assert.True(choice.Draft.DueAt > lateNow));
    }

    [Theory]
    [InlineData("0点开会")]
    [InlineData("24点开会")]
    [InlineData("9点60分开会")]
    [InlineData("999999999999999999999分钟后喝水")]
    public void Rejects_invalid_clock_values_and_duration_overflow(string text)
    {
        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Theory]
    [InlineData("每周五20分钟后写周报")]
    [InlineData("明天20分钟后开会")]
    [InlineData("下午3点20分钟后打电话")]
    public void Rejects_conflicting_time_expressions(string text)
    {
        Assert.IsType<ParseResult.Invalid>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));
    }

    [Fact]
    public void Resolves_an_invalid_dst_local_time_to_the_first_valid_minute()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var now = DateTimeOffset.Parse("2026-03-07T12:00:00-05:00");

        var result = Assert.IsType<ParseResult.Success>(
            new ChineseTimeParser().Parse("明天2点半提醒我检查", now, eastern));

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T03:00:00-04:00"), result.Draft.DueAt);
    }

    [Fact]
    public void Resolves_an_ambiguous_dst_local_time_to_the_earlier_utc_instant()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var now = DateTimeOffset.Parse("2026-10-31T12:00:00-04:00");

        var result = Assert.IsType<ParseResult.Success>(
            new ChineseTimeParser().Parse("明天1点半提醒我检查", now, eastern));

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:30:00-04:00"), result.Draft.DueAt);
    }

    [Theory]
    [InlineData("待会下午3点提醒我喝水", 15, "下午3点")]
    [InlineData("待会中午12点提醒我喝水", 12, "中午12点")]
    [InlineData("待会12点提醒我喝水", 12, "12点")]
    public void Returns_distinct_future_choices_when_a_vague_relative_phrase_has_a_disambiguated_clock(
        string text, int hour, string label)
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse(text, Now, ChinaTimeZone));

        Assert.Equal(2, result.Choices.Count);
        Assert.Equal(2, result.Choices.Select(choice => choice.Draft.DueAt).Distinct().Count());
        Assert.All(result.Choices, choice =>
        {
            Assert.Equal("喝水", choice.Draft.Title);
            Assert.Equal(hour, choice.Draft.DueAt.Hour);
            Assert.True(choice.Draft.DueAt > Now);
            Assert.DoesNotContain("下午0点", choice.Label);
        });
        Assert.Contains(result.Choices, choice => choice.Label.Contains(label, StringComparison.Ordinal));
    }

    [Fact]
    public void Interprets_vague_evening_twelve_as_future_midnight()
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(
            new ChineseTimeParser().Parse("待会晚上12点提醒我喝水", Now, ChinaTimeZone));

        Assert.Equal(2, result.Choices.Count);
        Assert.All(result.Choices, choice =>
        {
            Assert.Equal("喝水", choice.Draft.Title);
            Assert.Equal(0, choice.Draft.DueAt.Hour);
            Assert.True(choice.Draft.DueAt > Now);
            Assert.Contains("晚上12点", choice.Label, StringComparison.Ordinal);
        });
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"), result.Choices[0].Draft.DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-31T00:00:00+08:00"), result.Choices[1].Draft.DueAt);
    }

    [Fact]
    public void Interprets_direct_evening_twelve_as_midnight()
    {
        var result = Assert.IsType<ParseResult.Success>(
            new ChineseTimeParser().Parse("明天晚上12点提醒我检查", Now, ChinaTimeZone));

        Assert.Equal(DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"), result.Draft.DueAt);
    }
}
