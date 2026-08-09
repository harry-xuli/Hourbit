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

    private static readonly Regex RelativeDatePattern = new(
        "(?<date>今天|明天|明早)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseDatePattern = new(
        "(?<![\\d年])(?:(?<year>\\d{4})年)?(?<month>\\d{1,2})月(?<day>\\d{1,2})日(?!\\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericDatePattern = new(
        "(?<![A-Za-z\\d./+-])(?=(?:\\d{4}[/.-]|\\d{1,2}[/.-]\\d{1,2}[/.-]\\d{4}(?![A-Za-z\\d./+-])))(?<first>\\d{1,4})(?<separator>[/.-])(?<second>\\d{1,2})\\k<separator>(?<third>\\d{1,4})(?![A-Za-z\\d./+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericDateMarkerPattern = new(
        "(?<![A-Za-z\\d+-])(?:[+-]?\\d{4,}[/.-]+[+-]?\\d+[/.-]+[+-]?\\d+|[+-]?\\d+[/.-]+[+-]?\\d+[/.-]+[+-]?\\d{4,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseDateMarkerPattern = new(
        "(?<![A-Za-z\\d])(?:(?:[+-]?\\d+)年)?[+-]?\\d+月[+-]?\\d+日(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseClockPattern = new(
        "(?<![\\d+-])(?<period>早上|上午|中午|下午|晚上)?\\s*(?<hour>\\d{1,2})点(?:(?<half>半)|(?<minute>\\d{1,2})分?)?(?![\\d点分+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColonClockPattern = new(
        "(?<![\\d:+-])(?<hour>\\d{1,2}):(?<minute>\\d{2})(?![\\d:+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseClockMarkerPattern = new(
        "(?<!\\d)[+-]?\\d+点",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColonClockMarkerPattern = new(
        "(?<!\\d)[+-]?\\d+:",
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

    public ParseResult Parse(
        string text,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);

        var originalText = text ?? string.Empty;
        var normalized = Normalize(originalText);
        if (normalized.Length == 0)
        {
            return Invalid(originalText, "提醒内容不能为空。");
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var recurrenceMatch = RecurrencePattern.Match(normalized);
        var durationMatches = DurationPattern.Matches(normalized);
        if (durationMatches.Count > 1)
        {
            return Invalid(originalText, "只能指定一个相对时长。");
        }

        var durationMatch = durationMatches.Count == 1 ? durationMatches[0] : Match.Empty;
        if (durationMatch.Success)
        {
            if (recurrenceMatch.Success)
            {
                return Invalid(originalText, "重复提醒不能使用相对时长。");
            }

            if (HasDateToken(normalized) || HasClockToken(normalized) ||
                AmbiguousPattern.IsMatch(normalized))
            {
                return Invalid(originalText, "相对时长不能与日期或钟点组合。");
            }

            var durationTitle = ExtractTitle(RemoveMatches(normalized, durationMatch));
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

        var dateMatches = FindDateMatches(normalized);
        if (dateMatches.Count > 1)
        {
            return Invalid(originalText, "只能指定一个日期。");
        }

        var dateMatch = dateMatches.Count == 1 ? dateMatches[0] : Match.Empty;
        DateOnly? date = null;
        if (dateMatch.Success && !TryParseDate(dateMatch, localNow, culture, out date))
        {
            return Invalid(originalText, "日期格式无效。");
        }

        if (HasMalformedDateToken(normalized, dateMatches))
        {
            return Invalid(originalText, "日期格式无效。");
        }

        var clockMatches = FindClockMatches(normalized);
        if (HasMalformedClockToken(normalized, clockMatches))
        {
            return Invalid(originalText, "时间格式无效。");
        }

        if (clockMatches.Count > 1)
        {
            return Invalid(originalText, "只能指定一个钟点。");
        }

        var clockMatch = clockMatches.Count == 1 ? clockMatches[0] : Match.Empty;
        var time = default(TimeOnly);
        if (clockMatch.Success && !TryParseClock(clockMatch, out time))
        {
            return Invalid(originalText, "时间格式无效。");
        }

        if (recurrenceMatch.Success && dateMatch.Success && clockMatch.Success)
        {
            return Invalid(originalText, "重复提醒不能同时指定日期。");
        }

        if (recurrenceMatch.Success && !clockMatch.Success)
        {
            var todoTitle = ExtractTitle(RemoveMatches(normalized, dateMatch));
            if (!TryValidateTitle(todoTitle, originalText, out var todoTitleInvalid))
            {
                return todoTitleInvalid;
            }

            return Todo(todoTitle, date);
        }

        RecurrenceRule? recurrenceRule = null;
        var recurrence = clockMatch.Success &&
                         TryExtractRecurrence(recurrenceMatch, out recurrenceRule);
        var schedulingMatches = recurrence
            ? new[] { recurrenceMatch, dateMatch, clockMatch }.Where(match => match.Success).ToArray()
            : new[] { dateMatch, clockMatch }.Where(match => match.Success).ToArray();
        var remaining = RemoveMatches(normalized, schedulingMatches);

        if (!clockMatch.Success)
        {
            var ambiguousMatch = AmbiguousPattern.Match(remaining);
            if (ambiguousMatch.Success && !dateMatch.Success)
            {
                var ambiguousTitle = ExtractTitle(RemoveMatches(remaining, ambiguousMatch));
                if (!TryValidateTitle(ambiguousTitle, originalText, out var ambiguousTitleInvalid))
                {
                    return ambiguousTitleInvalid;
                }

                return Ambiguous(originalText, ambiguousTitle, now, zone,
                    ambiguousMatch.Groups["phrase"].Value, null, string.Empty, null);
            }

            var todoTitle = ExtractTitle(remaining);
            if (!TryValidateTitle(todoTitle, originalText, out var todoTitleInvalid))
            {
                return todoTitleInvalid;
            }

            return Todo(todoTitle, date);
        }

        var title = ExtractTitle(remaining);
        if (!TryValidateTitle(title, originalText, out var titleInvalid))
        {
            return titleInvalid;
        }

        var ambiguousClockMatch = AmbiguousClockPattern.Match(remaining);
        if (ambiguousClockMatch.Success)
        {
            var ambiguousTitle = ExtractTitle(RemoveMatches(remaining, ambiguousClockMatch));
            if (!TryValidateTitle(ambiguousTitle, originalText, out var ambiguousTitleInvalid))
            {
                return ambiguousTitleInvalid;
            }

            return Ambiguous(originalText, ambiguousTitle, now, zone,
                ambiguousClockMatch.Groups["phrase"].Value, time,
                clockMatch.Groups["period"].Value, recurrence ? recurrenceRule : null);
        }

        var recurrenceValue = recurrence ? recurrenceRule! with { Time = time } : null;
        var due = recurrence
            ? NextOccurrence(recurrenceValue!, time, now, zone)
            : date is null
                ? NextOneOffOccurrence(time, now, zone)
                : ResolveLocal(date.Value.ToDateTime(time), zone);

        if (due <= now && IsYearlessChineseDate(dateMatch))
        {
            var localDate = DateOnly.FromDateTime(localNow.DateTime);
            if (localDate == DateOnly.MaxValue)
            {
                return Invalid(originalText, "提醒时间超出可用范围。");
            }

            var nextLocalDate = localDate.AddDays(1);
            if (!TryCreateNextAnnualDate(
                    dateMatch.Groups["month"].Value,
                    dateMatch.Groups["day"].Value,
                    nextLocalDate,
                    out var nextDate))
            {
                return Invalid(originalText, "日期格式无效。");
            }

            due = ResolveLocal(nextDate!.Value.ToDateTime(time), zone);
        }

        if (due <= now)
        {
            return Invalid(originalText, "提醒时间必须晚于当前时间。");
        }

        return Success(title, due, recurrenceValue);
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

    private static IReadOnlyList<Match> FindDateMatches(string text) =>
        RelativeDatePattern.Matches(text).Cast<Match>()
            .Concat(ChineseDatePattern.Matches(text).Cast<Match>())
            .Concat(NumericDatePattern.Matches(text).Cast<Match>())
            .OrderBy(match => match.Index)
            .ToArray();

    private static IReadOnlyList<Match> FindClockMatches(string text) =>
        ChineseClockPattern.Matches(text).Cast<Match>()
            .Concat(ColonClockPattern.Matches(text).Cast<Match>())
            .OrderBy(match => match.Index)
            .ToArray();

    private static bool HasDateToken(string text) =>
        FindDateMatches(text).Count != 0 ||
        NumericDateMarkerPattern.IsMatch(text) ||
        ChineseDateMarkerPattern.IsMatch(text);

    private static bool HasClockToken(string text) =>
        FindClockMatches(text).Count != 0 ||
        ChineseClockMarkerPattern.IsMatch(text) ||
        ColonClockMarkerPattern.IsMatch(text);

    private static bool HasMalformedDateToken(
        string text,
        IReadOnlyList<Match> validDateMatches) =>
        NumericDateMarkerPattern.Matches(text).Cast<Match>()
            .Concat(ChineseDateMarkerPattern.Matches(text).Cast<Match>())
            .Any(candidate =>
            !validDateMatches.Any(valid =>
                Covers(valid, candidate)));

    private static bool HasMalformedClockToken(
        string text,
        IReadOnlyList<Match> validClockMatches) =>
        ChineseClockMarkerPattern.Matches(text).Cast<Match>()
            .Concat(ColonClockMarkerPattern.Matches(text).Cast<Match>())
            .Any(marker => !validClockMatches.Any(valid => Covers(valid, marker)));

    private static bool Covers(Match match, Match candidate) =>
        match.Index <= candidate.Index &&
        match.Index + match.Length >= candidate.Index + candidate.Length;

    private static bool TryParseDate(
        Match match,
        DateTimeOffset localNow,
        CultureInfo culture,
        out DateOnly? date)
    {
        date = null;
        if (match.Groups["date"].Success)
        {
            var localDate = DateOnly.FromDateTime(localNow.DateTime);
            date = match.Groups["date"].Value is "明天" or "明早"
                ? localDate.AddDays(1)
                : localDate;
            return true;
        }

        if (match.Groups["year"].Success)
        {
            return TryCreateDate(
                match.Groups["year"].Value,
                match.Groups["month"].Value,
                match.Groups["day"].Value,
                out date);
        }

        if (match.Groups["month"].Success && match.Groups["day"].Success)
        {
            return TryCreateNextAnnualDate(
                match.Groups["month"].Value,
                match.Groups["day"].Value,
                DateOnly.FromDateTime(localNow.DateTime),
                out date);
        }

        var firstText = match.Groups["first"].Value;
        var secondText = match.Groups["second"].Value;
        var thirdText = match.Groups["third"].Value;
        if (firstText.Length == 4)
        {
            return TryCreateDate(firstText, secondText, thirdText, out date);
        }

        if (thirdText.Length != 4)
        {
            return false;
        }

        if (!TryGetMonthFirst(culture.DateTimeFormat.ShortDatePattern, out var monthFirst))
        {
            return false;
        }

        return monthFirst
            ? TryCreateDate(thirdText, firstText, secondText, out date)
            : TryCreateDate(thirdText, secondText, firstText, out date);
    }

    private static bool TryGetMonthFirst(string pattern, out bool monthFirst)
    {
        monthFirst = false;
        var monthIndex = -1;
        var dayIndex = -1;
        char? quote = null;

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                index++;
                continue;
            }

            if (quote is not null)
            {
                if (current == quote)
                {
                    if (index + 1 < pattern.Length && pattern[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = null;
                    }
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == 'M' && monthIndex < 0)
            {
                monthIndex = index;
            }
            else if (current == 'd' && dayIndex < 0)
            {
                dayIndex = index;
            }
        }

        if (monthIndex < 0 || dayIndex < 0)
        {
            return false;
        }

        monthFirst = monthIndex < dayIndex;
        return true;
    }

    private static bool TryCreateDate(
        string yearText,
        string monthText,
        string dayText,
        out DateOnly? date)
    {
        date = null;
        if (!TryParseNonNegativeInteger(yearText, out var year) ||
            !TryParseNonNegativeInteger(monthText, out var month) ||
            !TryParseNonNegativeInteger(dayText, out var day))
        {
            return false;
        }

        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryCreateNextAnnualDate(
        string monthText,
        string dayText,
        DateOnly notBefore,
        out DateOnly? date)
    {
        date = null;
        if (!TryParseNonNegativeInteger(monthText, out var month) ||
            !TryParseNonNegativeInteger(dayText, out var day))
        {
            return false;
        }

        var lastYear = Math.Min(DateOnly.MaxValue.Year, notBefore.Year + 400);
        for (var year = notBefore.Year; year <= lastYear; year++)
        {
            try
            {
                var candidate = new DateOnly(year, month, day);
                if (candidate >= notBefore)
                {
                    date = candidate;
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Keep looking so a yearless February 29 can resolve to the next leap year.
            }
        }

        return false;
    }

    private static bool IsYearlessChineseDate(Match match) =>
        match.Success &&
        match.Groups["month"].Success &&
        match.Groups["day"].Success &&
        !match.Groups["year"].Success;

    private static bool TryParseClock(Match match, out TimeOnly time)
    {
        time = default;
        if (!TryParseNonNegativeInteger(match.Groups["hour"].Value, out var hour))
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

        var period = match.Groups["period"].Value;
        if (match.Value.Contains(':', StringComparison.Ordinal))
        {
            if (hour is < 0 or > 23)
            {
                return false;
            }

            time = new TimeOnly(hour, minute);
            return true;
        }

        if (period.Length == 0)
        {
            if (hour is < 0 or > 23)
            {
                return false;
            }

            time = new TimeOnly(hour, minute);
            return true;
        }

        if (period is "早上" or "上午")
        {
            if (hour is < 0 or > 12)
            {
                return false;
            }
        }
        else if (hour is < 1 or > 12)
        {
            return false;
        }

        hour = period switch
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

    private static ParseResult.Success Todo(string title, DateOnly? dueDate) =>
        new(new TodoDraft(title, dueDate, ReminderImportance.Normal));

    private static string RemoveMatches(string text, params Match[] matches)
    {
        foreach (var match in matches.Where(match => match.Success).OrderByDescending(match => match.Index))
        {
            text = text.Remove(match.Index, match.Length);
        }

        return text;
    }

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

    private static bool TryParseNonNegativeInteger(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static ParseResult.Invalid Invalid(string originalText, string message) => new(originalText, message);

    private static string Normalize(string text) => WhitespacePattern.Replace(
        text.Trim().Replace('，', ' ').Replace('。', ' ').Replace('！', ' ').Replace('？', ' ').Replace('：', ' '), " ");
}
