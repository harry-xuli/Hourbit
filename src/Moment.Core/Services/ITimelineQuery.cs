using Moment.Core.Analytics;
using Moment.Core.Domain;

namespace Moment.Core.Services;

public interface ITimelineQuery
{
    Task<TimelineSnapshot> GetTimelineAsync(
        LocalDateRange range,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CancellationToken ct);
}

public sealed record TimelineSnapshot(
    IReadOnlyList<TodoTimelineRow> Todos,
    IReadOnlyList<TimelineRow> Reminders,
    int TodosCompletedToday,
    int RemindersCompletedToday,
    int PastSevenDaysCompleted = 0,
    int NextFourteenDaysPlanned = 0);

public sealed record TodoTimelineRow(
    Guid TodoId,
    string Title,
    DateTimeOffset CreatedAt,
    DateOnly? DueDate,
    ReminderImportance Importance,
    bool IsCompleted,
    DateTimeOffset? CompletedAt);

public sealed record TimelineRow(
    Guid OccurrenceId,
    string Title,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    ReminderImportance Importance,
    OccurrenceState State,
    string? RecurrenceText);
