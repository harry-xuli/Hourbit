using Moment.Core.Domain;

namespace Moment.Core.Services;

public interface ITimelineQuery
{
    Task<TimelineSnapshot> GetTimelineAsync(
        DateOnly localDate, TimeZoneInfo zone, CancellationToken ct);
}

public sealed record TimelineSnapshot(
    IReadOnlyList<TodoTimelineRow> Todos,
    IReadOnlyList<TimelineRow> Reminders,
    int TodosCompletedToday,
    int RemindersCompletedToday);

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
