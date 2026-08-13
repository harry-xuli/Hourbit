using System.Collections.Immutable;
using System.Globalization;
using Hourbit.Core.Domain;

namespace Hourbit.Core.Analytics;

public sealed class AnalyticsQueryService
{
    private const int DailyBucketMaximumDays = 31;
    private const int WeeklyBucketMaximumDays = 180;

    private readonly IAnalyticsQuery _query;
    private readonly TimeProvider _timeProvider;
    private readonly CultureInfo _culture;

    public AnalyticsQueryService(
        IAnalyticsQuery query,
        TimeProvider? timeProvider = null,
        CultureInfo? culture = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _culture = culture ?? CultureInfo.CurrentCulture;
    }

    public async Task<AnalyticsSnapshot> CreateSnapshotAsync(
        LocalDateRange range,
        TimeZoneInfo zone,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(zone);
        if (range.Start > range.End)
            throw new ArgumentOutOfRangeException(nameof(range), "The start date cannot follow the end date.");
        if (range.End == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(range), "The inclusive end date is too large.");

        ct.ThrowIfCancellationRequested();
        var start = ResolveLocal(range.Start.ToDateTime(TimeOnly.MinValue), zone).ToUniversalTime();
        var end = ResolveLocal(range.End.AddDays(1).ToDateTime(TimeOnly.MinValue), zone).ToUniversalTime();
        var history = await _query.ReadAsync(start, end, includeDeleted: true, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var generatedAt = _timeProvider.GetUtcNow();
        var now = TimeZoneInfo.ConvertTime(generatedAt, zone);
        var completionActions = history.Actions
            .Where(static action => action.State == OccurrenceState.Completed)
            .GroupBy(static action => action.OccurrenceId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static action => action.HandledAt)
                    .ThenBy(static action => action.ActionId)
                    .Last().HandledAt);

        var details = new List<AnalyticsDetailRow>();
        foreach (var todo in history.Todos)
        {
            ct.ThrowIfCancellationRequested();
            if (!ShouldInclude(todo, range, zone))
                continue;

            details.Add(new AnalyticsDetailRow(
                todo.TodoId,
                todo.TodoId,
                AnalyticsItemType.Todo,
                todo.Title,
                null,
                null,
                todo.Importance,
                todo.CreatedAt,
                todo.DueDate,
                null,
                todo.CompletedAt,
                todo.DeletedAt,
                GetTodoStatus(todo, now)));
        }

        foreach (var reminder in history.Reminders)
        {
            ct.ThrowIfCancellationRequested();
            DateTimeOffset? completedAt = null;
            if (reminder.State == OccurrenceState.Completed)
            {
                completedAt = completionActions.TryGetValue(reminder.OccurrenceId, out var actionAt)
                    ? actionAt
                    : reminder.HandledAt;
            }
            if (!ShouldInclude(reminder, completedAt, range, zone))
                continue;

            details.Add(new AnalyticsDetailRow(
                reminder.OccurrenceId,
                reminder.ItemId,
                AnalyticsItemType.Reminder,
                reminder.Title,
                reminder.Kind,
                reminder.State,
                reminder.Importance,
                reminder.CreatedAt,
                null,
                reminder.DueAt,
                completedAt,
                reminder.DeletedAt,
                GetReminderStatus(reminder)));
        }

        var orderedDetails = details
            .OrderBy(static row => row.IsDeleted)
            .ThenBy(row => row.DueDate ?? LocalDate(row.DueAt, zone) ?? DateOnly.MaxValue)
            .ThenBy(row => LocalTime(row.DueAt, zone))
            .ThenBy(static row => row.DueAt?.UtcTicks)
            .ThenBy(static row => row.ItemType)
            .ThenBy(static row => row.RecordId)
            .ToImmutableArray();

        var active = orderedDetails
            .Where(static row => !row.IsDeleted)
            .ToImmutableArray();
        var completed = active.Count(row =>
            row.Status == AnalyticsRecordStatus.Completed &&
            IsInRange(row.CompletedAt, range, zone));
        var overdue = active.Count(static row => row.Status == AnalyticsRecordStatus.Overdue);
        var futurePlanned = active.Count(row => IsFuturePlan(row, range, now, zone));
        var totals = new AnalyticsTotals(
            active.Length,
            completed,
            futurePlanned,
            overdue,
            orderedDetails.Count(static row => row.IsDeleted),
            active.Count(static row => row.ItemType == AnalyticsItemType.Todo),
            active.Count(static row => row.ItemType == AnalyticsItemType.Reminder),
            active.Count(static row => row.ItemType == AnalyticsItemType.Todo && row.DueDate is null));

        return new AnalyticsSnapshot(
            Guid.NewGuid(),
            generatedAt,
            range,
            zone.Id,
            totals,
            CreateStatusDistribution(active),
            CreateTypeDistribution(active),
            CreateImportanceDistribution(active),
            CreateTrend(range, active, zone, _culture.DateTimeFormat.FirstDayOfWeek),
            orderedDetails);
    }

    private static bool ShouldInclude(
        AnalyticsTodoHistoryRow todo,
        LocalDateRange range,
        TimeZoneInfo zone)
    {
        if (todo.DeletedAt is not null)
        {
            return (todo.DueDate >= range.Start && todo.DueDate <= range.End) ||
                   IsInRange(todo.CompletedAt, range, zone) ||
                   IsInRange(todo.DeletedAt, range, zone);
        }
        if (todo.DueDate is null)
            return true;
        if (todo.DueDate >= range.Start && todo.DueDate <= range.End)
            return true;
        if (IsInRange(todo.CompletedAt, range, zone) || IsInRange(todo.DeletedAt, range, zone))
            return true;
        return false;
    }

    private static bool ShouldInclude(
        AnalyticsReminderHistoryRow reminder,
        DateTimeOffset? completedAt,
        LocalDateRange range,
        TimeZoneInfo zone) =>
        IsInRange(reminder.DueAt, range, zone) ||
        IsInRange(completedAt, range, zone) ||
        IsInRange(reminder.DeletedAt, range, zone) ||
        (reminder.State == OccurrenceState.Missed && IsInRange(reminder.HandledAt, range, zone));

    private static AnalyticsRecordStatus GetTodoStatus(
        AnalyticsTodoHistoryRow todo,
        DateTimeOffset now)
    {
        if (todo.DeletedAt is not null)
            return AnalyticsRecordStatus.Deleted;
        if (todo.IsCompleted)
            return AnalyticsRecordStatus.Completed;
        return todo.DueDate is not null && todo.DueDate < DateOnly.FromDateTime(now.DateTime)
            ? AnalyticsRecordStatus.Overdue
            : AnalyticsRecordStatus.Incomplete;
    }

    private static AnalyticsRecordStatus GetReminderStatus(AnalyticsReminderHistoryRow reminder)
    {
        if (reminder.DeletedAt is not null)
            return AnalyticsRecordStatus.Deleted;
        return reminder.State switch
        {
            OccurrenceState.Completed => AnalyticsRecordStatus.Completed,
            OccurrenceState.Missed => AnalyticsRecordStatus.Overdue,
            _ => AnalyticsRecordStatus.Incomplete
        };
    }

    private static bool IsFuturePlan(
        AnalyticsDetailRow row,
        LocalDateRange range,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        if (row.Status != AnalyticsRecordStatus.Incomplete)
            return false;
        if (row.ItemType == AnalyticsItemType.Todo)
        {
            var today = DateOnly.FromDateTime(now.DateTime);
            return row.DueDate is not null &&
                   row.DueDate >= today && row.DueDate >= range.Start && row.DueDate <= range.End;
        }

        return row.ReminderState == OccurrenceState.Scheduled &&
               row.DueAt is not null && row.DueAt > now && IsInRange(row.DueAt, range, zone);
    }

    private static ImmutableArray<DistributionSlice> CreateStatusDistribution(
        ImmutableArray<AnalyticsDetailRow> rows) =>
        [
            new DistributionSlice("completed", "已完成", rows.Count(static row => row.Status == AnalyticsRecordStatus.Completed)),
            new DistributionSlice("incomplete", "未完成", rows.Count(static row => row.Status == AnalyticsRecordStatus.Incomplete)),
            new DistributionSlice("overdue", "已逾期", rows.Count(static row => row.Status == AnalyticsRecordStatus.Overdue))
        ];

    private static ImmutableArray<DistributionSlice> CreateTypeDistribution(
        ImmutableArray<AnalyticsDetailRow> rows) =>
        [
            new DistributionSlice("todo", "待办", rows.Count(static row => row.ItemType == AnalyticsItemType.Todo)),
            new DistributionSlice("reminder", "提醒", rows.Count(static row => row.ItemType == AnalyticsItemType.Reminder))
        ];

    private static ImmutableArray<DistributionSlice> CreateImportanceDistribution(
        ImmutableArray<AnalyticsDetailRow> rows) =>
        [
            new DistributionSlice("normal", "普通", rows.Count(static row => row.Importance == ReminderImportance.Normal)),
            new DistributionSlice("important", "重要", rows.Count(static row => row.Importance == ReminderImportance.Important))
        ];

    private static ImmutableArray<TrendBucket> CreateTrend(
        LocalDateRange range,
        ImmutableArray<AnalyticsDetailRow> rows,
        TimeZoneInfo zone,
        DayOfWeek firstDayOfWeek)
    {
        var buckets = CreateEmptyBuckets(range, firstDayOfWeek);
        return buckets.Select(bucket => bucket with
            {
                Completed = rows.Count(row =>
                    row.Status == AnalyticsRecordStatus.Completed &&
                    row.CompletedAt is not null &&
                    IsWithinBucket(row.CompletedAt.Value, bucket, zone))
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<TrendBucket> CreateEmptyBuckets(
        LocalDateRange range,
        DayOfWeek firstDayOfWeek)
    {
        var dayCount = range.End.DayNumber - range.Start.DayNumber + 1;
        if (dayCount <= DailyBucketMaximumDays)
        {
            return Enumerable.Range(0, dayCount)
                .Select(offset => range.Start.AddDays(offset))
                .Select(date => new TrendBucket(date, date, FormatDate(date), 0))
                .ToImmutableArray();
        }

        var buckets = new List<TrendBucket>();
        if (dayCount <= WeeklyBucketMaximumDays)
        {
            var start = range.Start;
            while (start <= range.End)
            {
                var daysUntilNextWeek = ((int)firstDayOfWeek - (int)start.DayOfWeek + 7) % 7;
                var daysInBucket = daysUntilNextWeek == 0 ? 7 : daysUntilNextWeek;
                var end = Min(start.AddDays(daysInBucket - 1), range.End);
                buckets.Add(new TrendBucket(start, end, $"{FormatDate(start)} – {FormatDate(end)}", 0));
                start = end.AddDays(1);
            }
            return buckets.ToImmutableArray();
        }

        var monthStart = range.Start;
        while (monthStart <= range.End)
        {
            var monthEnd = new DateOnly(monthStart.Year, monthStart.Month,
                DateTime.DaysInMonth(monthStart.Year, monthStart.Month));
            monthEnd = Min(monthEnd, range.End);
            buckets.Add(new TrendBucket(
                monthStart, monthEnd,
                monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture), 0));
            monthStart = monthEnd.AddDays(1);
        }
        return buckets.ToImmutableArray();
    }

    private static bool IsWithinBucket(
        DateTimeOffset value,
        TrendBucket bucket,
        TimeZoneInfo zone)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        return date >= bucket.Start && date <= bucket.End;
    }

    private static bool IsInRange(
        DateTimeOffset? value,
        LocalDateRange range,
        TimeZoneInfo zone)
    {
        if (value is null)
            return false;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, zone).DateTime);
        return date >= range.Start && date <= range.End;
    }

    private static DateOnly? LocalDate(DateTimeOffset? value, TimeZoneInfo zone) =>
        value is null
            ? null
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, zone).DateTime);

    private static TimeOnly LocalTime(DateTimeOffset? value, TimeZoneInfo zone) =>
        value is null
            ? TimeOnly.MinValue
            : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, zone).DateTime);

    private static DateOnly Min(DateOnly left, DateOnly right) => left <= right ? left : right;

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            return zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset))
                .MinBy(static candidate => candidate.UtcDateTime);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
