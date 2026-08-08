using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Domain;
using Moment.Core.Services;

namespace Moment.Infrastructure.Data;

internal enum TimelineQueryReadStage
{
    TodosRead,
    RemindersRead
}

public sealed class SqliteTimelineQuery : ITimelineQuery
{
    private readonly string _databasePath;
    private readonly Func<TimelineQueryReadStage, CancellationToken, Task> _readObserver;

    public SqliteTimelineQuery(string databasePath)
        : this(databasePath, static (_, _) => Task.CompletedTask)
    {
    }

    internal SqliteTimelineQuery(
        string databasePath,
        Func<TimelineQueryReadStage, CancellationToken, Task> readObserver)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException(
                "A database path is required.", nameof(databasePath))
            : databasePath;
        _readObserver = readObserver
            ?? throw new ArgumentNullException(nameof(readObserver));
    }

    public async Task<TimelineSnapshot> GetTimelineAsync(
        DateOnly localDate, TimeZoneInfo zone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = ResolveLocal(localDate.ToDateTime(TimeOnly.MinValue), zone);
        var end = ResolveLocal(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            _databasePath, ct, SqliteCacheMode.Private);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var todos = await ReadTodosAsync(
            connection, transaction, localDate, ct);
        await _readObserver(TimelineQueryReadStage.TodosRead, ct);
        var reminders = await ReadRemindersAsync(
            connection, transaction, start, end, ct);
        await _readObserver(TimelineQueryReadStage.RemindersRead, ct);
        var todosCompletedToday = await CountCompletedAsync(
            connection, transaction,
            "todos", "is_completed", 1, start, end, ct);
        var remindersCompletedToday = await CountCompletedAsync(
            connection, transaction,
            "occurrences", "state", (int)OccurrenceState.Completed,
            start, end, ct);
        await transaction.CommitAsync(ct);
        return new TimelineSnapshot(
            todos,
            reminders,
            todosCompletedToday,
            remindersCompletedToday);
    }

    private static async Task<IReadOnlyList<TodoTimelineRow>> ReadTodosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly localDate,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, created_at, due_date, importance,
                   is_completed, completed_at
            FROM todos
            WHERE deleted_at IS NULL
            ORDER BY is_completed,
                     CASE
                         WHEN due_date IS NULL THEN 3
                         WHEN due_date < $localDate THEN 0
                         WHEN due_date = $localDate THEN 1
                         ELSE 2
                     END,
                     CASE WHEN is_completed = 1 THEN completed_at END DESC,
                     due_date,
                     id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue(
            "$localDate", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var rows = new List<TodoTimelineRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TodoTimelineRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDateTimeOffset(reader.GetString(2)),
                reader.IsDBNull(3)
                    ? null
                    : DateOnly.ParseExact(
                        reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (ReminderImportance)reader.GetInt32(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6)
                    ? null
                    : ParseDateTimeOffset(reader.GetString(6))));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<TimelineRow>> ReadRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.id, i.title, o.due_at, i.kind, i.importance, o.state,
                   r.kind, r.days_of_week
            FROM occurrences o
            INNER JOIN items i ON i.id = o.item_id
            LEFT JOIN recurrence_rules r ON r.item_id = i.id
            WHERE o.deleted_at IS NULL
              AND o.due_at_utc >= $startUtc
              AND o.due_at_utc < $endUtc
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

    private static async Task<int> CountCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string stateColumn,
        int completedState,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var timestampColumn = table == "todos" ? "completed_at" : "handled_at";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {table}
            WHERE {stateColumn} = $completedState
              AND deleted_at IS NULL
              AND {timestampColumn} IS NOT NULL
              AND julianday({timestampColumn}) >= julianday($startUtc)
              AND julianday({timestampColumn}) < julianday($endUtc);
            """;
        command.Parameters.AddWithValue("$completedState", completedState);
        command.Parameters.AddWithValue("$startUtc", FormatUtc(start));
        command.Parameters.AddWithValue("$endUtc", FormatUtc(end));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
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

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
