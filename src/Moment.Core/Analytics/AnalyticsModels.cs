using System.Collections.Immutable;
using Moment.Core.Domain;

namespace Moment.Core.Analytics;

public sealed record LocalDateRange(DateOnly Start, DateOnly End);

public sealed record AnalyticsSnapshot
{
    public AnalyticsSnapshot(
        Guid snapshotId,
        DateTimeOffset generatedAt,
        LocalDateRange range,
        string timeZoneId,
        AnalyticsTotals totals,
        IEnumerable<DistributionSlice> status,
        IEnumerable<DistributionSlice> itemTypes,
        IEnumerable<DistributionSlice> importance,
        IEnumerable<TrendBucket> trend,
        IEnumerable<AnalyticsDetailRow> details)
    {
        SnapshotId = snapshotId;
        GeneratedAt = generatedAt;
        Range = range ?? throw new ArgumentNullException(nameof(range));
        TimeZoneId = timeZoneId ?? throw new ArgumentNullException(nameof(timeZoneId));
        Totals = totals ?? throw new ArgumentNullException(nameof(totals));
        Status = ToImmutable(status, nameof(status));
        ItemTypes = ToImmutable(itemTypes, nameof(itemTypes));
        Importance = ToImmutable(importance, nameof(importance));
        Trend = ToImmutable(trend, nameof(trend));
        Details = ToImmutable(details, nameof(details));
    }

    public Guid SnapshotId { get; }
    public DateTimeOffset GeneratedAt { get; }
    public LocalDateRange Range { get; }
    public string TimeZoneId { get; }
    public AnalyticsTotals Totals { get; }
    public ImmutableArray<DistributionSlice> Status { get; }
    public ImmutableArray<DistributionSlice> ItemTypes { get; }
    public ImmutableArray<DistributionSlice> Importance { get; }
    public ImmutableArray<TrendBucket> Trend { get; }
    public ImmutableArray<AnalyticsDetailRow> Details { get; }

    private static ImmutableArray<T> ToImmutable<T>(
        IEnumerable<T> values,
        string parameterName) =>
        (values ?? throw new ArgumentNullException(parameterName)).ToImmutableArray();
}

public sealed record AnalyticsTotals(
    int Active,
    int Completed,
    int FuturePlanned,
    int Overdue,
    int Deleted,
    int Todos,
    int Reminders,
    int UndatedTodos);

public sealed record DistributionSlice(string Key, string Label, int Count);

public sealed record TrendBucket(
    DateOnly Start,
    DateOnly End,
    string Label,
    int Completed);

public enum AnalyticsItemType
{
    Todo,
    Reminder
}

public enum AnalyticsRecordStatus
{
    Incomplete,
    Completed,
    Overdue,
    Deleted
}

public sealed record AnalyticsDetailRow(
    Guid RecordId,
    Guid ItemId,
    AnalyticsItemType ItemType,
    string Title,
    ReminderKind? ReminderKind,
    OccurrenceState? ReminderState,
    ReminderImportance Importance,
    DateTimeOffset CreatedAt,
    DateOnly? DueDate,
    DateTimeOffset? DueAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DeletedAt,
    AnalyticsRecordStatus Status)
{
    public bool IsDeleted => DeletedAt.HasValue;
}

public sealed record AnalyticsHistory
{
    public AnalyticsHistory(
        IEnumerable<AnalyticsTodoHistoryRow> todos,
        IEnumerable<AnalyticsReminderHistoryRow> reminders,
        IEnumerable<AnalyticsActionHistoryRow> actions)
    {
        Todos = (todos ?? throw new ArgumentNullException(nameof(todos))).ToImmutableArray();
        Reminders = (reminders ?? throw new ArgumentNullException(nameof(reminders))).ToImmutableArray();
        Actions = (actions ?? throw new ArgumentNullException(nameof(actions))).ToImmutableArray();
    }

    public ImmutableArray<AnalyticsTodoHistoryRow> Todos { get; }
    public ImmutableArray<AnalyticsReminderHistoryRow> Reminders { get; }
    public ImmutableArray<AnalyticsActionHistoryRow> Actions { get; }

    public static AnalyticsHistory Empty { get; } = new([], [], []);
}

public sealed record AnalyticsTodoHistoryRow(
    Guid TodoId,
    string Title,
    DateTimeOffset CreatedAt,
    DateOnly? DueDate,
    ReminderImportance Importance,
    bool IsCompleted,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DeletedAt);

public sealed record AnalyticsReminderHistoryRow(
    Guid ItemId,
    Guid OccurrenceId,
    string Title,
    ReminderKind Kind,
    ReminderImportance Importance,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    OccurrenceState State,
    DateTimeOffset? HandledAt,
    DateTimeOffset? DeletedAt);

public sealed record AnalyticsActionHistoryRow(
    Guid ActionId,
    Guid OccurrenceId,
    OccurrenceState State,
    DateTimeOffset HandledAt);
