using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Analytics;
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
    private const int MaximumTimeZoneOffsetHours = 14;

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
        LocalDateRange range,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(zone);
        if (range.Start > range.End)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (range.End == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(range));

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        if (today.DayNumber > DateOnly.MaxValue.DayNumber - 14)
            throw new ArgumentOutOfRangeException(nameof(now));
        var start = ResolveLocal(range.Start.ToDateTime(TimeOnly.MinValue), zone);
        var end = ResolveLocal(range.End.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var todayStart = ResolveLocal(today.ToDateTime(TimeOnly.MinValue), zone);
        var tomorrowStart = ResolveLocal(today.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var pastStart = ResolveLocal(today.AddDays(-6).ToDateTime(TimeOnly.MinValue), zone);
        var futureEnd = ResolveLocal(today.AddDays(14).ToDateTime(TimeOnly.MinValue), zone);

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            _databasePath, ct, SqliteCacheMode.Private);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var todos = await ReadTodosAsync(
            connection, transaction, range, today, start, end, zone, ct);
        await _readObserver(TimelineQueryReadStage.TodosRead, ct);
        var reminders = await ReadRemindersAsync(
            connection, transaction, start, end, ct);
        await _readObserver(TimelineQueryReadStage.RemindersRead, ct);
        var todoCompletionTimes = await ReadTodoCompletionTimesAsync(
            connection, transaction, pastStart, tomorrowStart, ct);
        var reminderCompletionTimes = await ReadReminderCompletionTimesAsync(
            connection, transaction, pastStart, tomorrowStart, ct);
        var todosCompletedToday = CountWithin(
            todoCompletionTimes, todayStart, tomorrowStart);
        var remindersCompletedToday = CountWithin(
            reminderCompletionTimes, todayStart, tomorrowStart);
        var pastSevenDaysCompleted =
            CountWithin(todoCompletionTimes, pastStart, tomorrowStart) +
            CountWithin(reminderCompletionTimes, pastStart, tomorrowStart);
        var nextFourteenDaysPlanned = await CountFuturePlansAsync(
            connection, transaction, today, now, futureEnd, ct);
        await transaction.CommitAsync(ct);
        return new TimelineSnapshot(
            todos,
            reminders,
            todosCompletedToday,
            remindersCompletedToday,
            pastSevenDaysCompleted,
            nextFourteenDaysPlanned);
    }

    private static async Task<IReadOnlyList<TodoTimelineRow>> ReadTodosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalDateRange range,
        DateOnly today,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo zone,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, created_at, due_date, importance,
                   is_completed, completed_at
            FROM todos
            WHERE deleted_at IS NULL
              AND (
                  (is_completed = 0 AND due_date IS NULL)
                  OR (is_completed = 0 AND due_date < $today)
                  OR (due_date >= $rangeStartDate AND due_date <= $rangeEndDate)
                  OR (is_completed = 1
                      AND completed_at IS NOT NULL
                      AND completed_at >= $rangeSafeStartText
                      AND completed_at < $rangeSafeEndText)
              )
            ORDER BY is_completed,
                     CASE
                         WHEN due_date IS NULL THEN 3
                         WHEN due_date < $today THEN 0
                         WHEN due_date >= $rangeStartDate
                              AND due_date <= $rangeEndDate THEN 1
                         ELSE 2
                     END,
                     CASE WHEN is_completed = 1 THEN completed_at END DESC,
                     due_date,
                     id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue(
            "$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$rangeStartDate", range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$rangeEndDate", range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddSafeTimestampRangeParameters(
            command, "range", rangeStart, rangeEnd);

        var rows = new List<TodoTimelineRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new TodoTimelineRow(
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
                    : ParseDateTimeOffset(reader.GetString(6)));
            if (ShouldIncludeTodo(row, range, today, zone))
                rows.Add(row);
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

    private static bool ShouldIncludeTodo(
        TodoTimelineRow row,
        LocalDateRange range,
        DateOnly today,
        TimeZoneInfo zone)
    {
        if (!row.IsCompleted)
        {
            return row.DueDate is null || row.DueDate < today ||
                   IsWithin(row.DueDate.Value, range);
        }

        if (row.DueDate is not null && IsWithin(row.DueDate.Value, range))
            return true;
        if (row.CompletedAt is null)
            return false;
        var completedDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(row.CompletedAt.Value, zone).DateTime);
        return IsWithin(completedDate, range);
    }

    private static bool IsWithin(DateOnly value, LocalDateRange range) =>
        value >= range.Start && value <= range.End;

    private static async Task<IReadOnlyList<DateTimeOffset>> ReadTodoCompletionTimesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT completed_at
            FROM todos
            WHERE is_completed = 1
              AND deleted_at IS NULL
              AND completed_at IS NOT NULL
              AND completed_at >= $safeStartText
              AND completed_at < $safeEndText;
            """;
        AddSafeTimestampRangeParameters(command, string.Empty, start, end);
        return await ReadTimestampColumnAsync(command, ct);
    }

    private static async Task<IReadOnlyList<DateTimeOffset>> ReadReminderCompletionTimesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH completed AS (
                SELECT COALESCE(
                    (
                        SELECT a.handled_at
                        FROM action_log a
                        WHERE a.occurrence_id = o.id
                          AND a.state = $completed
                        ORDER BY julianday(a.handled_at) DESC, a.id COLLATE NOCASE DESC
                        LIMIT 1
                    ),
                    o.handled_at) AS completed_at
                FROM occurrences o
                WHERE o.state = $completed
                  AND o.deleted_at IS NULL
            )
            SELECT completed_at
            FROM completed
            WHERE completed_at IS NOT NULL
              AND completed_at >= $safeStartText
              AND completed_at < $safeEndText;
            """;
        command.Parameters.AddWithValue("$completed", (int)OccurrenceState.Completed);
        AddSafeTimestampRangeParameters(command, string.Empty, start, end);
        return await ReadTimestampColumnAsync(command, ct);
    }

    private static async Task<IReadOnlyList<DateTimeOffset>> ReadTimestampColumnAsync(
        SqliteCommand command,
        CancellationToken ct)
    {
        var values = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values.Add(ParseDateTimeOffset(reader.GetString(0)));
        return values;
    }

    private static int CountWithin(
        IReadOnlyList<DateTimeOffset> values,
        DateTimeOffset start,
        DateTimeOffset end) =>
        values.Count(value => value >= start && value < end);

    private static void AddSafeTimestampRangeParameters(
        SqliteCommand command,
        string prefix,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var safeStartDate = DateOnly.FromDateTime(
            start.ToUniversalTime()
                .AddHours(-MaximumTimeZoneOffsetHours)
                .UtcDateTime);
        var safeEndDateExclusive = DateOnly.FromDateTime(
                end.ToUniversalTime()
                    .AddHours(MaximumTimeZoneOffsetHours)
                    .UtcDateTime)
            .AddDays(1);
        var safeStartParameter = string.IsNullOrEmpty(prefix)
            ? "$safeStartText"
            : $"${prefix}SafeStartText";
        var safeEndParameter = string.IsNullOrEmpty(prefix)
            ? "$safeEndText"
            : $"${prefix}SafeEndText";
        command.Parameters.AddWithValue(
            safeStartParameter,
            safeStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            safeEndParameter,
            safeEndDateExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static async Task<int> CountFuturePlansAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly today,
        DateTimeOffset now,
        DateTimeOffset futureEnd,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*)
                 FROM todos
                 WHERE deleted_at IS NULL
                   AND is_completed = 0
                   AND due_date IS NOT NULL
                   AND due_date >= $today
                   AND due_date < $futureEndDate)
                +
                (SELECT COUNT(*)
                 FROM occurrences
                 WHERE deleted_at IS NULL
                   AND state = $scheduled
                   AND due_at_utc > $nowUtc
                   AND due_at_utc < $futureEndUtc);
            """;
        command.Parameters.AddWithValue(
            "$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$futureEndDate",
            today.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue("$nowUtc", FormatUtc(now));
        command.Parameters.AddWithValue("$futureEndUtc", FormatUtc(futureEnd));
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
