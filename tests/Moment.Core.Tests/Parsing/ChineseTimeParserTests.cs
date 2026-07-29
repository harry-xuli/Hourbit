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
}
