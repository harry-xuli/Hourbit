using System.Globalization;
using System.Text.RegularExpressions;
using Moment.Core.Domain;

namespace Moment.Core.Parsing;

public sealed class ChineseTimeParser : IChineseTimeParser
{
    private static readonly Regex RecurrencePattern = new(
        "^(?<recurrence>每天|每个工作日|每周(?<weekday>[一二三四五六日天]))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationPattern = new(
        "(?<amount>\\d+)\\s*(?<unit>分钟|小时)后",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DatePattern = new(
        "(?<date>今天|明天|明早)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClockPattern = new(
        "(?<period>上午|中午|下午|晚上)?\\s*(?<hour>\\d{1,2})点(?:(?<half>半)|(?<minute>\\d{1,2})分?)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReminderPrefixPattern = new(
        "提醒我", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        "\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AmbiguousPattern = new(
        "(?<phrase>晚上|待会|下周)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AmbiguousClockPattern = new(
        "(?<phrase>待会|下周)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ParseResult Parse(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var originalText = text ?? string.Empty;
        var normalized = Normalize(originalText);
        if (normalized.Length == 0)
        {
            return Invalid(originalText, "提醒内容不能为空。");
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var recurrenceMatch = RecurrencePattern.Match(normalized);
        var recurrence = TryExtractRecurrence(recurrenceMatch, out var recurrenceRule);
        var remaining = recurrenceMatch.Success
            ? RecurrencePattern.Replace(normalized, string.Empty, 1)
            : normalized;

        var durationMatch = DurationPattern.Match(remaining);
        if (durationMatch.Success)
        {
            if (recurrence)
            {
                return Invalid(originalText, "重复提醒不能使用相对时长。");
            }

            if (DatePattern.IsMatch(remaining) || ClockPattern.IsMatch(remaining) ||
                AmbiguousPattern.IsMatch(remaining))
            {
                return Invalid(originalText, "相对时长不能与日期或钟点组合。");
            }

            var durationTitle = ExtractTitle(DurationPattern.Replace(remaining, string.Empty));
            if (!TryValidateTitle(durationTitle, originalText, out var invalid))
            {
                return invalid;
            }

            if (!TryParsePositiveInteger(durationMatch.Groups["amount"].Value, out var amount))
            {
                return Invalid(originalText, "相对时长必须是正整数。");
            }

            try
            {
                var relativeDue = durationMatch.Groups["unit"].Value == "分钟"
                    ? now.AddMinutes(amount)
                    : now.AddHours(amount);
                return Success(durationTitle, relativeDue, null);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Invalid(originalText, "相对时长超出可用范围。");
            }
        }

        var dateMatch = DatePattern.Match(remaining);
        var dateToken = dateMatch.Success ? dateMatch.Groups["date"].Value : null;
        if (dateMatch.Success)
        {
            remaining = DatePattern.Replace(remaining, string.Empty, 1);
        }

        var clockMatch = ClockPattern.Match(remaining);
        if (!clockMatch.Success)
        {
            var ambiguousMatch = AmbiguousPattern.Match(remaining);
            if (ambiguousMatch.Success)
            {
                var ambiguousTitle = ExtractTitle(
                    AmbiguousPattern.Replace(remaining, string.Empty, 1));
                if (!TryValidateTitle(ambiguousTitle, originalText, out var ambiguousTitleInvalid))
                {
                    return ambiguousTitleInvalid;
                }

                return Ambiguous(originalText, ambiguousTitle, now, zone,
                    ambiguousMatch.Groups["phrase"].Value, null, string.Empty, recurrenceRule);
            }

            return Invalid(originalText, "未找到明确的提醒时间。");
        }

        var titleAfterClock = ClockPattern.Replace(remaining, string.Empty, 1);
        var title = ExtractTitle(titleAfterClock);
        if (!TryValidateTitle(title, originalText, out var titleInvalid))
        {
            return titleInvalid;
        }

        if (!TryParseClock(clockMatch, out var time))
        {
            return Invalid(originalText, "时间格式无效。");
        }

        var ambiguousClockMatch = AmbiguousClockPattern.Match(remaining);
        if (ambiguousClockMatch.Success)
        {
            var ambiguousTitle = ExtractTitle(AmbiguousClockPattern.Replace(
                ClockPattern.Replace(remaining, string.Empty, 1), string.Empty, 1));
            if (!TryValidateTitle(ambiguousTitle, originalText, out var ambiguousTitleInvalid))
            {
                return ambiguousTitleInvalid;
            }

            return Ambiguous(originalText, ambiguousTitle, now, zone,
                ambiguousClockMatch.Groups["phrase"].Value, time,
                clockMatch.Groups["period"].Value, recurrenceRule);
        }

        var recurrenceValue = recurrence ? recurrenceRule! with { Time = time } : null;
        var due = recurrence
            ? NextOccurrence(recurrenceValue!, time, now, zone)
            : ResolveDateTime(dateToken, time, localNow, zone);

        if (due is null)
        {
            return Invalid(originalText, "时间格式无效。");
        }

        if (due <= now)
        {
            return Invalid(originalText, "提醒时间必须晚于当前时间。");
        }

        return Success(title, due.Value, recurrenceValue);
    }

    private static bool TryExtractRecurrence(Match match, out RecurrenceRule? recurrence)
    {
        recurrence = null;
        if (!match.Success)
        {
            return false;
        }

        recurrence = match.Groups["recurrence"].Value switch
        {
            "每天" => RecurrenceRule.Daily(TimeOnly.MinValue),
            "每个工作日" => RecurrenceRule.Weekdays(TimeOnly.MinValue),
            _ => RecurrenceRule.Weekly([MapDayOfWeek(match.Groups["weekday"].Value)], TimeOnly.MinValue)
        };
        return true;
    }

    private static DayOfWeek MapDayOfWeek(string weekday) => weekday switch
    {
        "一" => DayOfWeek.Monday,
        "二" => DayOfWeek.Tuesday,
        "三" => DayOfWeek.Wednesday,
        "四" => DayOfWeek.Thursday,
        "五" => DayOfWeek.Friday,
        "六" => DayOfWeek.Saturday,
        "日" or "天" => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(weekday))
    };

    private static bool TryParseClock(Match match, out TimeOnly time)
    {
        time = default;
        if (!TryParsePositiveInteger(match.Groups["hour"].Value, out var hour))
        {
            return false;
        }

        var minute = 0;
        if (match.Groups["half"].Success)
        {
            minute = 30;
        }
        else if (match.Groups["minute"].Success &&
                 !int.TryParse(match.Groups["minute"].Value, NumberStyles.None,
                     CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        if (minute is < 0 or > 59)
        {
            return false;
        }

        hour = match.Groups["period"].Value switch
        {
            "下午" or "晚上" when hour is >= 1 and <= 11 => hour + 12,
            "晚上" when hour == 12 => 0,
            "中午" when hour is >= 1 and <= 11 => hour + 12,
            _ => hour
        };

        if (hour is < 0 or > 23)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    private static DateTimeOffset? ResolveDateTime(
        string? dateToken, TimeOnly time, DateTimeOffset localNow, TimeZoneInfo zone)
    {
        var date = localNow.Date;
        if (dateToken is "明天" or "明早")
        {
            date = date.AddDays(1);
        }

        return ResolveLocal(date + time.ToTimeSpan(), zone);
    }

    private static DateTimeOffset NextOccurrence(
        RecurrenceRule recurrence, TimeOnly time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var rule = recurrence with { Time = time };
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;

        for (var days = 0; days <= 370; days++)
        {
            var candidateLocal = localNow.Date.AddDays(days) + time.ToTimeSpan();
            if (!Allows(rule, candidateLocal.DayOfWeek))
            {
                continue;
            }

            var candidate = ResolveLocal(candidateLocal, zone);
            if (candidate > now)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No recurrence occurrence found within 370 days.");
    }

    private static bool Allows(RecurrenceRule rule, DayOfWeek day) => rule.Kind switch
    {
        RecurrenceKind.Daily => true,
        RecurrenceKind.Weekdays => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        RecurrenceKind.Weekly => rule.DaysOfWeek.Contains(day),
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

    private static ParseResult.Ambiguous Ambiguous(
        string originalText,
        string title,
        DateTimeOffset now,
        TimeZoneInfo zone,
        string phrase,
        TimeOnly? clock,
        string clockPeriod,
        RecurrenceRule? recurrence)
    {
        var choices = phrase switch
        {
            "晚上" => TimeChoices(title, now, zone, recurrence, (20, 0), (21, 0), "晚上8点", "晚上9点"),
            "待会" when clock is not null => ClockChoices(title, now, zone, clock.Value, clockPeriod),
            "待会" => RelativeChoices(title, now, 15, 30),
            _ => NextWeekChoices(title, now, zone, clock)
        };

        return new(originalText, choices);
    }

    private static IReadOnlyList<ParseChoice> TimeChoices(
        string title,
        DateTimeOffset now,
        TimeZoneInfo zone,
        RecurrenceRule? recurrence,
        (int Hour, int Minute) first,
        (int Hour, int Minute) second,
        string firstLabel,
        string secondLabel) =>
        [CreateTimeChoice(title, now, zone, recurrence, first, firstLabel),
         CreateTimeChoice(title, now, zone, recurrence, second, secondLabel)];

    private static ParseChoice CreateTimeChoice(
        string title,
        DateTimeOffset now,
        TimeZoneInfo zone,
        RecurrenceRule? recurrence,
        (int Hour, int Minute) time,
        string label)
    {
        var choiceTime = new TimeOnly(time.Hour, time.Minute);
        var rule = recurrence is null ? null : recurrence with { Time = choiceTime };
        var due = rule is null
            ? NextOneOffOccurrence(choiceTime, now, zone)
            : NextOccurrence(rule, choiceTime, now, zone);

        return new(label, CreateDraft(title, due, rule));
    }

    private static IReadOnlyList<ParseChoice> RelativeChoices(
        string title, DateTimeOffset now, int firstMinutes, int secondMinutes) =>
        [new($"{firstMinutes}分钟后", CreateDraft(title, now.AddMinutes(firstMinutes))),
         new($"{secondMinutes}分钟后", CreateDraft(title, now.AddMinutes(secondMinutes)))];

    private static IReadOnlyList<ParseChoice> ClockChoices(
        string title, DateTimeOffset now, TimeZoneInfo zone, TimeOnly clock, string period)
    {
        if (period.Length != 0 || clock.Hour == 12)
        {
            return DateChoices(title, now, zone, clock, FormatClock(clock, period));
        }

        var alternativeHour = clock.Hour is >= 1 and <= 11 ? clock.Hour + 12 : clock.Hour;
        return TimeChoices(title, now, zone, null, (clock.Hour, clock.Minute),
            (alternativeHour, clock.Minute), $"{clock.Hour}点", $"下午{alternativeHour - 12}点");
    }

    private static IReadOnlyList<ParseChoice> DateChoices(
        string title, DateTimeOffset now, TimeZoneInfo zone, TimeOnly clock, string label)
    {
        var firstDue = NextOneOffOccurrence(clock, now, zone);
        var nextDate = TimeZoneInfo.ConvertTime(firstDue, zone).Date.AddDays(1);
        var secondDue = ResolveLocal(nextDate + clock.ToTimeSpan(), zone);
        return
        [new($"下一次{label}", CreateDraft(title, firstDue)),
         new($"次日{label}", CreateDraft(title, secondDue))];
    }

    private static IReadOnlyList<ParseChoice> NextWeekChoices(
        string title, DateTimeOffset now, TimeZoneInfo zone, TimeOnly? clock)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)localNow.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        var monday = localNow.Date.AddDays(daysUntilMonday);
        var friday = monday.AddDays(4);
        var firstTime = clock ?? new TimeOnly(9, 0);
        var secondTime = clock ?? new TimeOnly(18, 0);
        return
        [new($"下周一{FormatTime(firstTime)}", CreateDraft(title,
             ResolveLocal(monday + firstTime.ToTimeSpan(), zone))),
         new($"下周五{FormatTime(secondTime)}", CreateDraft(title,
             ResolveLocal(friday + secondTime.ToTimeSpan(), zone)))];
    }

    private static DateTimeOffset NextOneOffOccurrence(TimeOnly time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var localCandidate = localNow.Date + time.ToTimeSpan();
        var candidate = ResolveLocal(localCandidate, zone);
        return candidate > now ? candidate : ResolveLocal(localCandidate.AddDays(1), zone);
    }

    private static string FormatTime(TimeOnly time) => time.Minute == 0
        ? $"{time.Hour}点"
        : $"{time.Hour}点{time.Minute}分";

    private static string FormatClock(TimeOnly time, string period)
    {
        var hour = period == "晚上" && time.Hour == 0
            ? 12
            : period is "下午" or "晚上" && time.Hour is >= 13 and <= 23
                ? time.Hour - 12
                : time.Hour;
        var clock = new TimeOnly(hour, time.Minute);
        return $"{period}{FormatTime(clock)}";
    }

    private static ReminderDraft CreateDraft(
        string title, DateTimeOffset due, RecurrenceRule? recurrence = null) =>
        new(title, due, ReminderKind.Countdown, ReminderImportance.Normal, recurrence);

    private static ParseResult.Success Success(string title, DateTimeOffset due, RecurrenceRule? recurrence) =>
        new(new ReminderDraft(title, due, ReminderKind.Countdown, ReminderImportance.Normal, recurrence));

    private static string ExtractTitle(string text) => WhitespacePattern.Replace(
        ReminderPrefixPattern.Replace(text, string.Empty).Trim(), " ");

    private static bool TryValidateTitle(string title, string originalText, out ParseResult.Invalid invalid)
    {
        if (title.Length == 0)
        {
            invalid = Invalid(originalText, "提醒标题不能为空。");
            return false;
        }

        if (title.Length > 200)
        {
            invalid = Invalid(originalText, "提醒标题不能超过200个字符。");
            return false;
        }

        invalid = null!;
        return true;
    }

    private static bool TryParsePositiveInteger(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static ParseResult.Invalid Invalid(string originalText, string message) => new(originalText, message);

    private static string Normalize(string text) => WhitespacePattern.Replace(
        text.Trim().Replace('，', ' ').Replace('。', ' ').Replace('！', ' ').Replace('？', ' ').Replace('：', ' '), " ");
}
