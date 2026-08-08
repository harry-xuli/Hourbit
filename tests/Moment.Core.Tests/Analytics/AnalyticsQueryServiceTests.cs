using System.Globalization;
using Moment.Core.Analytics;
using Moment.Core.Domain;

namespace Moment.Core.Tests.Analytics;

public sealed class AnalyticsQueryServiceTests
{
    [Fact]
    public async Task CreateSnapshot_converts_inclusive_leap_and_DST_dates_to_one_half_open_UTC_query()
    {
        var query = new RecordingAnalyticsQuery();
        var generatedAt = DateTimeOffset.Parse("2024-02-28T23:00:00Z", CultureInfo.InvariantCulture);
        var range = new LocalDateRange(new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 10));
        var zone = CreateEasternTestZone(2024);
        var service = new AnalyticsQueryService(query, new FixedTimeProvider(generatedAt), CultureInfo.InvariantCulture);

        var snapshot = await service.CreateSnapshotAsync(range, zone, CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2024-02-29T05:00:00Z", CultureInfo.InvariantCulture), query.Start);
        Assert.Equal(DateTimeOffset.Parse("2024-03-11T04:00:00Z", CultureInfo.InvariantCulture), query.End);
        Assert.True(query.IncludeDeleted);
        Assert.Equal(1, query.CallCount);
        Assert.Equal(range, snapshot.Range);
        Assert.Equal(zone.Id, snapshot.TimeZoneId);
        Assert.Equal(generatedAt, snapshot.GeneratedAt);
        Assert.NotEqual(Guid.Empty, snapshot.SnapshotId);
    }

    [Fact]
    public async Task CreateSnapshot_applies_completion_plan_overdue_deleted_and_distribution_rules()
    {
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00+08:00", CultureInfo.InvariantCulture);
        var recurringItemId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var completedOccurrence = Reminder(
            "20000000-0000-0000-0000-000000000001", Guid.NewGuid(), "跨期完成",
            "2026-07-20T09:00:00+08:00", "2026-07-20T10:00:00+08:00",
            OccurrenceState.Completed, "2026-07-28T10:00:00+08:00", ReminderImportance.Important);
        var recurringFirst = Reminder(
            "20000000-0000-0000-0000-000000000002", recurringItemId, "每日复盘",
            "2026-07-01T09:00:00+08:00", "2026-07-27T18:00:00+08:00",
            OccurrenceState.Completed, "2026-07-27T18:05:00+08:00");
        var recurringSecond = Reminder(
            "20000000-0000-0000-0000-000000000003", recurringItemId, "每日复盘",
            "2026-07-01T09:00:00+08:00", "2026-07-29T18:00:00+08:00",
            OccurrenceState.Completed, "2026-07-29T18:05:00+08:00");
        var query = new RecordingAnalyticsQuery
        {
            Result = new AnalyticsHistory(
                [
                    Todo("30000000-0000-0000-0000-000000000001", "无日期已完成", null, true,
                        "2026-07-30T08:00:00+08:00"),
                    Todo("30000000-0000-0000-0000-000000000002", "未来待办", new DateOnly(2026, 8, 5)),
                    Todo("30000000-0000-0000-0000-000000000003", "逾期待办", new DateOnly(2026, 7, 31),
                        importance: ReminderImportance.Important),
                    Todo("30000000-0000-0000-0000-000000000004", "已删除待办", new DateOnly(2026, 8, 2),
                        deletedAt: "2026-08-01T09:00:00+08:00")
                ],
                [
                    completedOccurrence,
                    Reminder("20000000-0000-0000-0000-000000000004", Guid.NewGuid(), "未来提醒",
                        "2026-07-01T09:00:00+08:00", "2026-08-03T14:00:00+08:00", OccurrenceState.Scheduled),
                    Reminder("20000000-0000-0000-0000-000000000005", Guid.NewGuid(), "错过提醒",
                        "2026-07-01T09:00:00+08:00", "2026-07-31T14:00:00+08:00", OccurrenceState.Missed,
                        "2026-08-01T08:00:00+08:00", ReminderImportance.Important),
                    recurringFirst,
                    recurringSecond,
                    Reminder("20000000-0000-0000-0000-000000000006", Guid.NewGuid(), "已删除提醒",
                        "2026-07-01T09:00:00+08:00", "2026-08-04T14:00:00+08:00", OccurrenceState.Scheduled,
                        deletedAt: "2026-08-01T09:30:00+08:00")
                ],
                [
                    Action("40000000-0000-0000-0000-000000000001", completedOccurrence.OccurrenceId,
                        OccurrenceState.Completed, "2026-07-28T10:00:00+08:00"),
                    Action("40000000-0000-0000-0000-000000000002", recurringFirst.OccurrenceId,
                        OccurrenceState.Completed, "2026-07-27T18:05:00+08:00"),
                    Action("40000000-0000-0000-0000-000000000003", recurringSecond.OccurrenceId,
                        OccurrenceState.Completed, "2026-07-29T18:05:00+08:00")
                ])
        };
        var service = new AnalyticsQueryService(query, new FixedTimeProvider(now), CultureInfo.InvariantCulture);

        var snapshot = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 7, 26), new DateOnly(2026, 8, 14)),
            FixedZone(), CancellationToken.None);

        Assert.Equal(new AnalyticsTotals(8, 4, 2, 2, 2, 3, 5, 1), snapshot.Totals);
        Assert.Equal(
            [("completed", 4), ("incomplete", 2), ("overdue", 2)],
            snapshot.Status.Select(slice => (slice.Key, slice.Count)));
        Assert.Equal(
            [("todo", 3), ("reminder", 5)],
            snapshot.ItemTypes.Select(slice => (slice.Key, slice.Count)));
        Assert.Equal(
            [("normal", 5), ("important", 3)],
            snapshot.Importance.Select(slice => (slice.Key, slice.Count)));
        Assert.Equal(10, snapshot.Details.Count);
        Assert.Equal(2, snapshot.Details.Count(row => row.IsDeleted));
        Assert.Equal(2, snapshot.Details.Count(row => row.ItemId == recurringItemId));
        Assert.Equal(4, snapshot.Trend.Sum(bucket => bucket.Completed));
        Assert.DoesNotContain(snapshot.Trend, bucket => bucket.Label.Contains("无日期", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSnapshot_uses_Windows_first_day_of_week_for_adaptive_weekly_buckets()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;
        var query = new RecordingAnalyticsQuery();
        var service = new AnalyticsQueryService(query, new FixedTimeProvider(DateTimeOffset.UnixEpoch), culture);

        var snapshot = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 8, 5), new DateOnly(2026, 9, 15)),
            FixedZone(), CancellationToken.None);

        Assert.Equal("2026-08-05 – 2026-08-09", snapshot.Trend[0].Label);
        Assert.Equal("2026-08-10 – 2026-08-16", snapshot.Trend[1].Label);
        Assert.Equal("2026-09-14 – 2026-09-15", snapshot.Trend[^1].Label);
        Assert.All(snapshot.Trend, bucket => Assert.Equal(0, bucket.Completed));
    }

    [Fact]
    public async Task CreateSnapshot_uses_daily_then_monthly_buckets_and_returns_useful_zero_data()
    {
        var service = new AnalyticsQueryService(
            new RecordingAnalyticsQuery(), new FixedTimeProvider(DateTimeOffset.UnixEpoch), CultureInfo.InvariantCulture);

        var daily = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2024, 2, 27), new DateOnly(2024, 3, 2)),
            TimeZoneInfo.Utc, CancellationToken.None);
        var monthly = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal(
            ["2024-02-27", "2024-02-28", "2024-02-29", "2024-03-01", "2024-03-02"],
            daily.Trend.Select(bucket => bucket.Label));
        Assert.Equal(12, monthly.Trend.Count);
        Assert.Equal("2026-01", monthly.Trend[0].Label);
        Assert.Equal("2026-12", monthly.Trend[^1].Label);
        Assert.Equal(new AnalyticsTotals(0, 0, 0, 0, 0, 0, 0, 0), daily.Totals);
        Assert.All(daily.Status, slice => Assert.Equal(0, slice.Count));
        Assert.Empty(daily.Details);
    }

    [Fact]
    public async Task CreateSnapshot_rejects_reversed_range_before_querying()
    {
        var query = new RecordingAnalyticsQuery();
        var service = new AnalyticsQueryService(query, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 1)),
            TimeZoneInfo.Utc, CancellationToken.None));

        Assert.Equal(0, query.CallCount);
    }

    [Fact]
    public async Task CreateSnapshot_excludes_deleted_undated_todo_without_an_event_in_the_range()
    {
        var query = new RecordingAnalyticsQuery
        {
            Result = new AnalyticsHistory(
                [Todo(
                    "50000000-0000-0000-0000-000000000001", "旧删除待办", null,
                    deletedAt: "2026-07-01T09:00:00+08:00")],
                [],
                [])
        };
        var service = new AnalyticsQueryService(
            query,
            new FixedTimeProvider(DateTimeOffset.Parse(
                "2026-08-01T12:00:00+08:00", CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture);

        var snapshot = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7)),
            FixedZone(), CancellationToken.None);

        Assert.Equal(0, snapshot.Totals.Deleted);
        Assert.Empty(snapshot.Details);
    }

    [Fact]
    public async Task CreateSnapshot_counts_only_scheduled_reminders_as_future_plans()
    {
        var query = new RecordingAnalyticsQuery
        {
            Result = new AnalyticsHistory(
                [],
                [
                    Reminder(
                        "60000000-0000-0000-0000-000000000001", Guid.NewGuid(), "已忽略",
                        "2026-07-01T09:00:00+08:00", "2026-08-03T09:00:00+08:00",
                        OccurrenceState.Ignored, "2026-08-01T09:00:00+08:00"),
                    Reminder(
                        "60000000-0000-0000-0000-000000000002", Guid.NewGuid(), "已推迟",
                        "2026-07-01T09:00:00+08:00", "2026-08-04T09:00:00+08:00",
                        OccurrenceState.Snoozed, "2026-08-01T10:00:00+08:00")
                ],
                [])
        };
        var service = new AnalyticsQueryService(
            query,
            new FixedTimeProvider(DateTimeOffset.Parse(
                "2026-08-01T08:00:00+08:00", CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture);

        var snapshot = await service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 14)),
            FixedZone(), CancellationToken.None);

        Assert.Equal(0, snapshot.Totals.FuturePlanned);
    }

    [Fact]
    public async Task CreateSnapshot_honors_pre_cancelled_token_without_querying()
    {
        var query = new RecordingAnalyticsQuery();
        var service = new AnalyticsQueryService(query, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateSnapshotAsync(
            new LocalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1)),
            TimeZoneInfo.Utc, source.Token));

        Assert.Equal(0, query.CallCount);
    }

    private static AnalyticsTodoHistoryRow Todo(
        string id,
        string title,
        DateOnly? dueDate,
        bool completed = false,
        string? completedAt = null,
        ReminderImportance importance = ReminderImportance.Normal,
        string? deletedAt = null) =>
        new(
            Guid.Parse(id), title,
            DateTimeOffset.Parse("2026-07-01T08:00:00+08:00", CultureInfo.InvariantCulture),
            dueDate, importance, completed,
            completedAt is null ? null : DateTimeOffset.Parse(completedAt, CultureInfo.InvariantCulture),
            deletedAt is null ? null : DateTimeOffset.Parse(deletedAt, CultureInfo.InvariantCulture));

    private static AnalyticsReminderHistoryRow Reminder(
        string occurrenceId,
        Guid itemId,
        string title,
        string createdAt,
        string dueAt,
        OccurrenceState state,
        string? handledAt = null,
        ReminderImportance importance = ReminderImportance.Normal,
        string? deletedAt = null) =>
        new(
            itemId, Guid.Parse(occurrenceId), title, ReminderKind.Plan, importance,
            DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(dueAt, CultureInfo.InvariantCulture), state,
            handledAt is null ? null : DateTimeOffset.Parse(handledAt, CultureInfo.InvariantCulture),
            deletedAt is null ? null : DateTimeOffset.Parse(deletedAt, CultureInfo.InvariantCulture));

    private static AnalyticsActionHistoryRow Action(
        string actionId,
        Guid occurrenceId,
        OccurrenceState state,
        string handledAt) =>
        new(Guid.Parse(actionId), occurrenceId, state,
            DateTimeOffset.Parse(handledAt, CultureInfo.InvariantCulture));

    private static TimeZoneInfo FixedZone() =>
        TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-analytics", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    private static TimeZoneInfo CreateEasternTestZone(int year)
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(year, 1, 1), new DateTime(year, 12, 31),
            TimeSpan.FromHours(1), daylightStart, daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            $"Eastern-analytics-{year}", TimeSpan.FromHours(-5), "Eastern",
            "Eastern", "Eastern DST", [rule]);
    }

    private sealed class RecordingAnalyticsQuery : IAnalyticsQuery
    {
        public AnalyticsHistory Result { get; init; } = AnalyticsHistory.Empty;
        public DateTimeOffset Start { get; private set; }
        public DateTimeOffset End { get; private set; }
        public bool IncludeDeleted { get; private set; }
        public int CallCount { get; private set; }

        public Task<AnalyticsHistory> ReadAsync(
            DateTimeOffset utcStartInclusive,
            DateTimeOffset utcEndExclusive,
            bool includeDeleted,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Start = utcStartInclusive;
            End = utcEndExclusive;
            IncludeDeleted = includeDeleted;
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
