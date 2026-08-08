using Moment.Core.Domain;

namespace Moment.Core.Analytics;

public sealed record LocalDateRange(DateOnly Start, DateOnly End);

public sealed record AnalyticsSnapshot(
    Guid SnapshotId,
    DateTimeOffset GeneratedAt,
    LocalDateRange Range,
    string TimeZoneId,
    AnalyticsTotals Totals,
    IReadOnlyList<DistributionSlice> Status,
    IReadOnlyList<DistributionSlice> ItemTypes,
    IReadOnlyList<DistributionSlice> Importance,
    IReadOnlyList<TrendBucket> Trend,
    IReadOnlyList<AnalyticsDetailRow> Details);

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

public sealed record AnalyticsHistory(
    IReadOnlyList<AnalyticsTodoHistoryRow> Todos,
    IReadOnlyList<AnalyticsReminderHistoryRow> Reminders,
    IReadOnlyList<AnalyticsActionHistoryRow> Actions)
{
    public static AnalyticsHistory Empty { get; } = new(
        Array.Empty<AnalyticsTodoHistoryRow>(),
        Array.Empty<AnalyticsReminderHistoryRow>(),
        Array.Empty<AnalyticsActionHistoryRow>());
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
