using System.Globalization;
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
    [InlineData("8点晨会", "晨会", "2026-07-30T08:00:00+08:00")]
    public void Preserves_supported_relative_and_next_valid_time_behavior(
        string text, string title, string due)
    {
        var draft = Reminder(text);

        Assert.Equal(title, draft.Title);
        Assert.Equal(DateTimeOffset.Parse(due), draft.DueAt);
        Assert.Equal(ReminderKind.Countdown, draft.Kind);
        Assert.Equal(ReminderImportance.Normal, draft.Importance);
        Assert.Null(draft.Recurrence);
    }

    [Theory]
    [InlineData("zh-CN", "2026-08-05 14:30 发布版本", "2026-08-05T14:30:00+08:00")]
    [InlineData("en-US", "2026/08/05 14:30 发布版本", "2026-08-05T14:30:00+08:00")]
    [InlineData("en-GB", "2026.08.05 14:30 发布版本", "2026-08-05T14:30:00+08:00")]
    [InlineData("zh-CN", "2026年8月5日 14点30分 发布版本", "2026-08-05T14:30:00+08:00")]
    [InlineData("en-US", "08-05-2026 23:59 发布版本", "2026-08-05T23:59:00+08:00")]
    [InlineData("en-GB", "05.08.2026 00:00 发布版本", "2026-08-05T00:00:00+08:00")]
    public void Parses_absolute_date_and_time_using_the_supplied_culture(
        string cultureName, string text, string expectedDue)
    {
        var draft = Reminder(text, cultureName);

        Assert.Equal("发布版本", draft.Title);
        Assert.Equal(DateTimeOffset.Parse(expectedDue), draft.DueAt);
    }

    [Theory]
    [InlineData(
        "2026-08-09T10:25:00+08:00",
        "10月3日早上6点小朋友办宴",
        "小朋友办宴",
        "2026-10-03T06:00:00+08:00")]
    [InlineData(
        "2026-12-20T10:25:00+08:00",
        "1月2日早上6点新年安排",
        "新年安排",
        "2027-01-02T06:00:00+08:00")]
    [InlineData(
        "2026-10-03T10:25:00+08:00",
        "10月3日早上6点明年复诊",
        "明年复诊",
        "2027-10-03T06:00:00+08:00")]
    public void Parses_yearless_Chinese_dates_as_the_next_future_occurrence(
        string now, string text, string expectedTitle, string expectedDue)
    {
        var draft = Reminder(text, now: DateTimeOffset.Parse(now));

        Assert.Equal(expectedTitle, draft.Title);
        Assert.Equal(DateTimeOffset.Parse(expectedDue), draft.DueAt);
    }

    [Theory]
    [InlineData("10月3日 早上6点 闺女办事")]
    [InlineData("10月3号 早上6点 闺女办事")]
    [InlineData("2026年10月3号 早上6点 闺女办事")]
    public void Parses_Chinese_day_suffix_variants(string text)
    {
        var draft = Reminder(
            text,
            now: DateTimeOffset.Parse("2026-08-09T10:25:00+08:00"));

        Assert.Equal("闺女办事", draft.Title);
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-03T06:00:00+08:00"),
            draft.DueAt);
    }

    [Fact]
    public void Parses_a_yearless_Chinese_date_without_a_clock_as_a_dated_todo()
    {
        var draft = Todo("10月3日小朋友办宴");

        Assert.Equal("小朋友办宴", draft.Title);
        Assert.Equal(new DateOnly(2026, 10, 3), draft.DueDate);
    }

    [Fact]
    public void Parses_a_hao_suffixed_date_without_a_clock_as_a_dated_todo()
    {
        var draft = Todo("10月3号闺女办事");

        Assert.Equal("闺女办事", draft.Title);
        Assert.Equal(new DateOnly(2026, 10, 3), draft.DueDate);
    }

    [Theory]
    [InlineData("123月5日早上6点开会")]
    [InlineData("10月333日早上6点开会")]
    [InlineData("-10月3日早上6点开会")]
    [InlineData("10月+3日早上6点开会")]
    [InlineData("A123月5日早上6点开会")]
    [InlineData("A10月+3日早上6点开会")]
    [InlineData("A-10月3日早上6点开会")]
    [InlineData("A10月333日B早上6点开会")]
    [InlineData("13月3号早上6点开会")]
    [InlineData("123月5号早上6点开会")]
    [InlineData("10月+3号早上6点开会")]
    [InlineData("A-10月3号早上6点开会")]
    public void Rejects_malformed_Chinese_date_markers_instead_of_scheduling_only_the_clock(
        string text)
    {
        Assert.IsType<ParseResult.Invalid>(Parse(text));
    }

    [Fact]
    public void Returns_invalid_instead_of_overflowing_past_the_maximum_local_date()
    {
        var result = Parse(
            "12月31日 9点检查",
            now: DateTimeOffset.Parse("9999-12-31T10:00:00Z"),
            zone: TimeZoneInfo.Utc);

        Assert.IsType<ParseResult.Invalid>(result);
    }

    [Fact]
    public void Preserves_year_prefixed_title_text_that_is_not_a_date_token()
    {
        var draft = Todo("周年10月3日活动");

        Assert.Equal("周年10月3日活动", draft.Title);
        Assert.Null(draft.DueDate);
    }

    [Fact]
    public void Preserves_hao_in_ordinary_undated_title_text()
    {
        var draft = Todo("核对工号123");

        Assert.Equal("核对工号123", draft.Title);
        Assert.Null(draft.DueDate);
    }

    [Theory]
    [InlineData("en-US", "08/09/2026 14:30 复盘", "2026-08-09T14:30:00+08:00")]
    [InlineData("en-GB", "08/09/2026 14:30 复盘", "2026-09-08T14:30:00+08:00")]
    [InlineData("zh-CN", "08/09/2026 14:30 复盘", "2026-08-09T14:30:00+08:00")]
    [InlineData("en-US", "2026/08/09 14:30 复盘", "2026-08-09T14:30:00+08:00")]
    [InlineData("en-GB", "2026/08/09 14:30 复盘", "2026-08-09T14:30:00+08:00")]
    [InlineData("zh-CN", "2026/08/09 14:30 复盘", "2026-08-09T14:30:00+08:00")]
    public void Uses_short_date_order_only_when_the_four_digit_year_is_last(
        string cultureName, string text, string expectedDue)
    {
        Assert.Equal(DateTimeOffset.Parse(expectedDue), Reminder(text, cultureName).DueAt);
    }

    [Theory]
    [InlineData("2028-02-29 14:30 闰日复盘", "2028-02-29T14:30:00+08:00")]
    [InlineData("2028年2月29日 23点59分 闰日复盘", "2028-02-29T23:59:00+08:00")]
    public void Accepts_valid_leap_dates(string text, string expectedDue)
    {
        Assert.Equal(DateTimeOffset.Parse(expectedDue), Reminder(text).DueAt);
    }

    [Theory]
    [InlineData("2026-02-29 14:30 复盘")]
    [InlineData("2026-02-29 复盘")]
    [InlineData("2026/04/31 14:30 复盘")]
    [InlineData("31.04.2026 14:30 复盘")]
    [InlineData("2026年13月5日 14点30分 复盘")]
    [InlineData("2026-08-05 24:00 复盘")]
    [InlineData("2026-08-05 23:60 复盘")]
    [InlineData("2026/08-05 14:30 复盘")]
    public void Rejects_impossible_absolute_dates_and_out_of_range_24_hour_times(string text)
    {
        Assert.IsType<ParseResult.Invalid>(Parse(text, "en-GB"));
    }

    [Theory]
    [InlineData("00:00 午夜检查", "2026-07-30T00:00:00+08:00")]
    [InlineData("23:59 日终检查", "2026-07-29T23:59:00+08:00")]
    [InlineData("0点 午夜检查", "2026-07-30T00:00:00+08:00")]
    [InlineData("下午2点 发布", "2026-07-29T14:00:00+08:00")]
    [InlineData("晚上8点半 阅读", "2026-07-29T20:30:00+08:00")]
    public void Parses_24_hour_boundaries_and_existing_Chinese_periods(
        string text, string expectedDue)
    {
        Assert.Equal(DateTimeOffset.Parse(expectedDue), Reminder(text).DueAt);
    }

    [Theory]
    [InlineData("买牛奶", "买牛奶")]
    [InlineData("提醒我买牛奶", "买牛奶")]
    [InlineData("每天锻炼", "每天锻炼")]
    [InlineData("每周一整理房间", "每周一整理房间")]
    [InlineData("每周五晚上看书", "每周五晚上看书")]
    public void Returns_an_undated_non_recurring_todo_when_no_date_or_clock_is_present(
        string text, string expectedTitle)
    {
        var draft = Todo(text);

        Assert.Equal(expectedTitle, draft.Title);
        Assert.Null(draft.DueDate);
        Assert.Equal(ReminderImportance.Normal, draft.Importance);
    }

    [Theory]
    [InlineData("zh-CN", "2026-08-05 提交报告", "2026-08-05")]
    [InlineData("en-GB", "05/08/2026 提交报告", "2026-08-05")]
    [InlineData("zh-CN", "明天提交报告", "2026-07-30")]
    public void Returns_a_dated_todo_when_a_valid_date_has_no_time(
        string cultureName, string text, string expectedDueDate)
    {
        var draft = Todo(text, cultureName);

        Assert.Equal("提交报告", draft.Title);
        Assert.Equal(DateOnly.Parse(expectedDueDate, CultureInfo.InvariantCulture), draft.DueDate);
    }

    [Theory]
    [InlineData("晚上提醒我看书", "看书")]
    [InlineData("待会提醒我喝水", "喝水")]
    [InlineData("下周提醒我交报告", "交报告")]
    public void Returns_reminder_choices_for_ambiguous_phrases(string text, string title)
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(Parse(text));

        Assert.NotEmpty(result.Choices);
        Assert.All(result.Choices, choice =>
            Assert.Equal(title, Assert.IsType<ReminderDraft>(choice.Draft).Title));
    }

    [Theory]
    [InlineData("每个工作日18点下班", RecurrenceKind.Weekdays,
        "2026-07-29T18:00:00+08:00")]
    [InlineData("每周五下午4点写周报", RecurrenceKind.Weekly,
        "2026-07-31T16:00:00+08:00")]
    public void Preserves_timed_recurrence_rules(
        string text, RecurrenceKind recurrenceKind, string expectedDue)
    {
        var draft = Reminder(text);
        var recurrence = Assert.IsType<RecurrenceRule>(draft.Recurrence);

        Assert.Equal(recurrenceKind, recurrence.Kind);
        Assert.Equal(DateTimeOffset.Parse(expectedDue), draft.DueAt);
        Assert.Equal(TimeOnly.FromDateTime(draft.DueAt.DateTime), recurrence.Time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("今天8点开会")]
    [InlineData("晚上提醒我")]
    [InlineData("待会提醒我")]
    [InlineData("下周提醒我")]
    [InlineData("24点开会")]
    [InlineData("9点60分开会")]
    [InlineData("999999999999999999999分钟后喝水")]
    public void Rejects_blank_missing_title_past_or_invalid_input(string text)
    {
        Assert.IsType<ParseResult.Invalid>(Parse(text));
    }

    [Fact]
    public void Rejects_titles_longer_than_200_characters()
    {
        Assert.IsType<ParseResult.Invalid>(Parse($"20分钟后{new string('测', 201)}"));
        Assert.IsType<ParseResult.Invalid>(Parse($"晚上提醒我{new string('测', 201)}"));
    }

    [Theory]
    [InlineData("每周五20分钟后写周报")]
    [InlineData("明天20分钟后开会")]
    [InlineData("下午3点20分钟后打电话")]
    [InlineData("明天 2026-08-05 开会")]
    [InlineData("14:30 下午2点 开会")]
    [InlineData("20分钟后 2026-08-05 开会")]
    [InlineData("每天 2026-08-05 14:30 开会")]
    public void Rejects_conflicting_date_or_time_expressions(string text)
    {
        var invalid = Assert.IsType<ParseResult.Invalid>(Parse(text));

        Assert.Equal(text, invalid.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Theory]
    [InlineData("123点开会")]
    [InlineData("-1点开会")]
    [InlineData("+1点开会")]
    [InlineData("下午123点开会")]
    [InlineData("事项123点开会")]
    [InlineData("1点2分3开会")]
    [InlineData("1点2点开会")]
    [InlineData("1点+2开会")]
    [InlineData("123:00开会")]
    [InlineData("事项123:00开会")]
    [InlineData("-1:00开会")]
    [InlineData("+1:00开会")]
    [InlineData("14:300开会")]
    [InlineData("14:30:00开会")]
    [InlineData("14:30-1:00开会")]
    [InlineData("14:30+1开会")]
    public void Rejects_signed_overlong_embedded_or_adjacent_malformed_clock_tokens(string text)
    {
        var invalid = Assert.IsType<ParseResult.Invalid>(Parse(text));

        Assert.Equal(text, invalid.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Theory]
    [InlineData("明天 2026/08-05 开会")]
    [InlineData("明天 14:30:00 开会")]
    [InlineData("14:30 123:00 开会")]
    [InlineData("14点 1点2分3 开会")]
    public void Rejects_a_malformed_scheduling_token_even_with_a_valid_companion(string text)
    {
        var invalid = Assert.IsType<ParseResult.Invalid>(Parse(text));

        Assert.Equal(text, invalid.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Theory]
    [InlineData("2026-8-055 开会")]
    [InlineData("明天 2026-8-055 开会")]
    [InlineData("明天 2026-008-5 开会")]
    [InlineData("明天 12026-8-5 开会")]
    [InlineData("明天 2026-8-5x 开会")]
    [InlineData("明天 -2026-8-5 开会")]
    [InlineData("明天 +2026-8-5 开会")]
    [InlineData("明天 2026/8-5 开会")]
    [InlineData("明天 2026--8-5 开会")]
    [InlineData("明天 8-5-20261 开会")]
    [InlineData("明天 08-005-2026 开会")]
    [InlineData("明天 8/-5/2026 开会")]
    [InlineData("2026-08-05 2026-8-055 开会")]
    public void Rejects_every_uncovered_numeric_date_marker(string text)
    {
        var invalid = Assert.IsType<ParseResult.Invalid>(Parse(text));

        Assert.Equal(text, invalid.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Fact]
    public void Ignores_quoted_and_escaped_literals_when_reading_short_date_field_order()
    {
        var dayFirst = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        dayFirst.DateTimeFormat.ShortDatePattern = "'M''/d' dd/MM/yyyy";
        var monthFirst = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        monthFirst.DateTimeFormat.ShortDatePattern = "\\d/\\M MM/dd/yyyy";
        var doubleQuotedDayFirst = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        doubleQuotedDayFirst.DateTimeFormat.ShortDatePattern = "\"M/d\" dd/MM/yyyy";

        Assert.Equal(DateTimeOffset.Parse("2026-09-08T14:30:00+08:00"),
            Reminder("08/09/2026 14:30 复盘", dayFirst).DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-09T14:30:00+08:00"),
            Reminder("08/09/2026 14:30 复盘", monthFirst).DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T14:30:00+08:00"),
            Reminder("08/09/2026 14:30 复盘", doubleQuotedDayFirst).DueAt);
    }

    [Theory]
    [InlineData("发布 v1.2.3")]
    [InlineData("发布 v1.2.2026")]
    [InlineData("发布 build2026-8-5")]
    [InlineData("版本 2026.8")]
    [InlineData("价格 3.14159")]
    [InlineData("比例 1/2")]
    [InlineData("修复 M/d 格式")]
    public void Keeps_ordinary_title_text_that_is_not_a_date_or_clock_token(string text)
    {
        var draft = Todo(text);

        Assert.Equal(text, draft.Title);
        Assert.Null(draft.DueDate);
    }

    [Fact]
    public void Returns_time_preserving_future_choices_for_an_ambiguous_week()
    {
        var result = Assert.IsType<ParseResult.Ambiguous>(Parse("下周下午4点写周报"));

        Assert.All(result.Choices, choice =>
        {
            var draft = Assert.IsType<ReminderDraft>(choice.Draft);
            Assert.Equal("写周报", draft.Title);
            Assert.Equal(new TimeOnly(16, 0), TimeOnly.FromDateTime(draft.DueAt.DateTime));
            Assert.True(draft.DueAt > Now);
        });
    }

    [Theory]
    [InlineData("待会3点提醒我喝水", 3, 15, "3点")]
    [InlineData("待会下午3点提醒我喝水", 15, 15, "下午3点")]
    [InlineData("待会中午12点提醒我喝水", 12, 12, "中午12点")]
    [InlineData("待会12点提醒我喝水", 12, 12, "12点")]
    public void Returns_distinct_future_choices_for_a_vague_phrase_with_a_clock(
        string text, int firstExpectedHour, int secondExpectedHour, string expectedLabel)
    {
        var earlyNow = DateTimeOffset.Parse("2026-07-29T01:00:00+08:00");
        var result = Assert.IsType<ParseResult.Ambiguous>(Parse(text, now: earlyNow));
        var drafts = result.Choices.Select(choice => Assert.IsType<ReminderDraft>(choice.Draft)).ToArray();

        Assert.Equal(2, drafts.Length);
        Assert.Equal(2, drafts.Select(draft => draft.DueAt).Distinct().Count());
        Assert.Equal(new[] { firstExpectedHour, secondExpectedHour },
            drafts.Select(draft => draft.DueAt.Hour).Order());
        Assert.All(drafts, draft =>
        {
            Assert.Equal("喝水", draft.Title);
            Assert.True(draft.DueAt > earlyNow);
        });
        Assert.Contains(result.Choices,
            choice => choice.Label.Contains(expectedLabel, StringComparison.Ordinal));
    }

    [Fact]
    public void Interprets_direct_and_vague_evening_twelve_as_midnight()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"),
            Reminder("明天晚上12点提醒我检查").DueAt);

        var ambiguous = Assert.IsType<ParseResult.Ambiguous>(Parse("待会晚上12点提醒我喝水"));
        var drafts = ambiguous.Choices
            .Select(choice => Assert.IsType<ReminderDraft>(choice.Draft))
            .ToArray();
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"), drafts[0].DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-31T00:00:00+08:00"), drafts[1].DueAt);
    }

    [Fact]
    public void Resolves_invalid_and_ambiguous_dst_local_times_compatibly()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var springNow = DateTimeOffset.Parse("2026-03-07T12:00:00-05:00");
        var fallNow = DateTimeOffset.Parse("2026-10-31T12:00:00-04:00");

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T03:00:00-04:00"),
            Reminder("明天2点半提醒我检查", now: springNow, zone: eastern).DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:30:00-04:00"),
            Reminder("明天1点半提醒我检查", now: fallNow, zone: eastern).DueAt);
    }

    private static ReminderDraft Reminder(
        string text,
        string cultureName = "zh-CN",
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null)
    {
        var result = Assert.IsType<ParseResult.Success>(Parse(text, cultureName, now, zone));
        return Assert.IsType<ReminderDraft>(result.Draft);
    }

    private static ReminderDraft Reminder(
        string text,
        CultureInfo culture,
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null)
    {
        var result = Assert.IsType<ParseResult.Success>(Parse(text, culture, now, zone));
        return Assert.IsType<ReminderDraft>(result.Draft);
    }

    private static TodoDraft Todo(string text, string cultureName = "zh-CN")
    {
        var result = Assert.IsType<ParseResult.Success>(Parse(text, cultureName));
        return Assert.IsType<TodoDraft>(result.Draft);
    }

    private static ParseResult Parse(
        string text,
        string cultureName = "zh-CN",
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null) =>
        new ChineseTimeParser().Parse(
            text,
            now ?? Now,
            zone ?? ChinaTimeZone,
            CultureInfo.GetCultureInfo(cultureName));

    private static ParseResult Parse(
        string text,
        CultureInfo culture,
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null) =>
        new ChineseTimeParser().Parse(text, now ?? Now, zone ?? ChinaTimeZone, culture);
}
