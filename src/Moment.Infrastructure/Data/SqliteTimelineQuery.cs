using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Domain;
using Moment.Core.Services;

namespace Moment.Infrastructure.Data;

public sealed class SqliteTimelineQuery(string databasePath) : ITimelineQuery
{
    private readonly string _databasePath = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("A database path is required.", nameof(databasePath))
        : databasePath;

    public async Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
        DateOnly localDate, TimeZoneInfo zone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = ResolveLocal(localDate.ToDateTime(TimeOnly.MinValue), zone);
        var end = ResolveLocal(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.id, i.title, o.due_at, i.kind, i.importance, o.state,
                   r.kind, r.days_of_week
            FROM occurrences o
            INNER JOIN items i ON i.id = o.item_id
            LEFT JOIN recurrence_rules r ON r.item_id = i.id
            WHERE o.due_at_utc >= $startUtc AND o.due_at_utc < $endUtc
            ORDER BY o.due_at_utc, o.id;
            """;
        command.Parameters.AddWithValue("$startUtc", FormatUtc(start));
        command.Parameters.AddWithValue("$endUtc", FormatUtc(end));

        var rows = new List<TimelineRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TimelineRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                (ReminderKind)reader.GetInt32(3),
                (ReminderImportance)reader.GetInt32(4),
                (OccurrenceState)reader.GetInt32(5),
                ReadRecurrenceText(reader)));
        }
        return rows;
    }

    private static string? ReadRecurrenceText(SqliteDataReader reader)
    {
        if (reader.IsDBNull(6))
            return null;

        return (RecurrenceKind)reader.GetInt32(6) switch
        {
            RecurrenceKind.Daily => "每天",
            RecurrenceKind.Weekdays => "工作日（周一至周五）",
            RecurrenceKind.Weekly => FormatWeekly(reader.GetString(7)),
            _ => null
        };
    }

    private static string FormatWeekly(string days)
    {
        var labels = days.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => (DayOfWeek)int.Parse(value, CultureInfo.InvariantCulture))
            .OrderBy(static day => day == DayOfWeek.Sunday ? 7 : (int)day)
            .Select(static day => day switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日"
            });
        return $"每周（{string.Join("、", labels)}）";
    }

    private static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            return zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset))
                .MinBy(candidate => candidate.UtcDateTime);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
