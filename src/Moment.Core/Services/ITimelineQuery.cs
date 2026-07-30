using Moment.Core.Domain;

namespace Moment.Core.Services;

public interface ITimelineQuery
{
    Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
        DateOnly localDate, TimeZoneInfo zone, CancellationToken ct);
}

public sealed record TimelineRow(
    Guid OccurrenceId,
    string Title,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    ReminderImportance Importance,
    OccurrenceState State,
    string? RecurrenceText);
