using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Analytics;
using Moment.Core.Domain;

namespace Moment.Infrastructure.Data;

internal enum AnalyticsQueryReadStage
{
    TodosRead,
    RemindersRead,
    ActionsRead
}

public sealed class SqliteAnalyticsQuery : IAnalyticsQuery
{
    private readonly string _databasePath;
    private readonly Func<AnalyticsQueryReadStage, CancellationToken, Task> _readObserver;

    public SqliteAnalyticsQuery(string databasePath)
        : this(databasePath, static (_, _) => Task.CompletedTask)
    {
    }

    internal SqliteAnalyticsQuery(
        string databasePath,
        Func<AnalyticsQueryReadStage, CancellationToken, Task> readObserver)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("A database path is required.", nameof(databasePath))
            : databasePath;
        _readObserver = readObserver ?? throw new ArgumentNullException(nameof(readObserver));
    }

    public async Task<AnalyticsHistory> ReadAsync(
        DateTimeOffset utcStartInclusive,
        DateTimeOffset utcEndExclusive,
        bool includeDeleted,
        CancellationToken ct)
    {
        if (utcStartInclusive >= utcEndExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utcEndExclusive), "The exclusive end must follow the inclusive start.");
        }

        ct.ThrowIfCancellationRequested();
        var start = FormatUtc(utcStartInclusive);
        var end = FormatUtc(utcEndExclusive);
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            _databasePath, ct, SqliteCacheMode.Private).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var todos = await ReadTodosAsync(connection, transaction, includeDeleted, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.TodosRead, ct).ConfigureAwait(false);
        var reminders = await ReadRemindersAsync(
            connection, transaction, start, end, includeDeleted, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.RemindersRead, ct).ConfigureAwait(false);
        var actions = await ReadActionsAsync(
            connection, transaction, start, end, includeDeleted, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.ActionsRead, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new AnalyticsHistory(todos, reminders, actions);
    }

    private static async Task<IReadOnlyList<AnalyticsTodoHistoryRow>> ReadTodosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool includeDeleted,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, created_at, due_date, importance,
                   is_completed, completed_at, deleted_at
            FROM todos
            WHERE $includeDeleted = 1 OR deleted_at IS NULL
            ORDER BY id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);

        var rows = new List<AnalyticsTodoHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AnalyticsTodoHistoryRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDateTimeOffset(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseDateOnly(reader.GetString(3)),
                (ReminderImportance)reader.GetInt32(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : ParseDateTimeOffset(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseDateTimeOffset(reader.GetString(7))));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<AnalyticsReminderHistoryRow>> ReadRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string start,
        string end,
        bool includeDeleted,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.id, o.id, i.title, i.kind, i.importance, i.created_at,
                   o.due_at, o.state, o.handled_at, o.deleted_at
            FROM occurrences o
            INNER JOIN items i ON i.id = o.item_id
            WHERE ($includeDeleted = 1 OR o.deleted_at IS NULL)
              AND (
                    (o.due_at_utc >= $startUtc AND o.due_at_utc < $endUtc)
                    OR (o.handled_at IS NOT NULL
                        AND julianday(o.handled_at) >= julianday($startUtc)
                        AND julianday(o.handled_at) < julianday($endUtc))
                    OR (o.deleted_at IS NOT NULL
                        AND julianday(o.deleted_at) >= julianday($startUtc)
                        AND julianday(o.deleted_at) < julianday($endUtc))
                    OR EXISTS (
                        SELECT 1
                        FROM action_log a
                        WHERE a.occurrence_id = o.id
                          AND julianday(a.handled_at) >= julianday($startUtc)
                          AND julianday(a.handled_at) < julianday($endUtc)
                    )
                  )
            ORDER BY o.due_at_utc, o.id COLLATE NOCASE;
            """;
        AddRangeParameters(command, start, end, includeDeleted);

        var rows = new List<AnalyticsReminderHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AnalyticsReminderHistoryRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                (ReminderKind)reader.GetInt32(3),
                (ReminderImportance)reader.GetInt32(4),
                ParseDateTimeOffset(reader.GetString(5)),
                ParseDateTimeOffset(reader.GetString(6)),
                (OccurrenceState)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : ParseDateTimeOffset(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDateTimeOffset(reader.GetString(9))));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<AnalyticsActionHistoryRow>> ReadActionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string start,
        string end,
        bool includeDeleted,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.id, a.occurrence_id, a.state, a.handled_at
            FROM action_log a
            INNER JOIN occurrences o ON o.id = a.occurrence_id
            WHERE ($includeDeleted = 1 OR o.deleted_at IS NULL)
              AND julianday(a.handled_at) >= julianday($startUtc)
              AND julianday(a.handled_at) < julianday($endUtc)
            ORDER BY julianday(a.handled_at), a.id COLLATE NOCASE;
            """;
        AddRangeParameters(command, start, end, includeDeleted);

        var rows = new List<AnalyticsActionHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AnalyticsActionHistoryRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                (OccurrenceState)reader.GetInt32(2),
                ParseDateTimeOffset(reader.GetString(3))));
        }
        return rows;
    }

    private static void AddRangeParameters(
        SqliteCommand command,
        string start,
        string end,
        bool includeDeleted)
    {
        command.Parameters.AddWithValue("$startUtc", start);
        command.Parameters.AddWithValue("$endUtc", end);
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateOnly ParseDateOnly(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
