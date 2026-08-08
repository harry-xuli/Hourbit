using System.Collections.Immutable;
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
    private const int MaximumTimeZoneOffsetHours = 14;

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
        var bounds = AnalyticsRangeBounds.Create(utcStartInclusive, utcEndExclusive);
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            _databasePath, ct, SqliteCacheMode.Private).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var todos = await ReadTodosAsync(
            connection, transaction, bounds, includeDeleted, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.TodosRead, ct).ConfigureAwait(false);
        var actions = await ReadActionsAsync(
            connection, transaction, bounds, includeDeleted, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.ActionsRead, ct).ConfigureAwait(false);
        var actionOccurrenceIds = actions
            .Select(static action => action.OccurrenceId)
            .ToHashSet();
        var reminders = await ReadRemindersAsync(
            connection, transaction, bounds, includeDeleted,
            actionOccurrenceIds, ct).ConfigureAwait(false);
        await _readObserver(AnalyticsQueryReadStage.RemindersRead, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new AnalyticsHistory(todos, reminders, actions);
    }

    private static async Task<ImmutableArray<AnalyticsTodoHistoryRow>> ReadTodosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalyticsRangeBounds bounds,
        bool includeDeleted,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var deletedEligibility = includeDeleted ? string.Empty : "deleted_at IS NULL AND";
        var deletedCandidate = includeDeleted
            ? "OR (deleted_at >= $safeStartText AND deleted_at < $safeEndText)"
            : string.Empty;
        command.CommandText = $"""
            SELECT id, title, created_at, due_date, importance,
                   is_completed, completed_at, deleted_at
            FROM todos
            WHERE {deletedEligibility} (
                    (deleted_at IS NULL AND due_date IS NULL)
                    OR (due_date >= $candidateDateStart
                        AND due_date < $candidateDateEnd)
                    OR (completed_at >= $safeStartText
                        AND completed_at < $safeEndText)
                    {deletedCandidate}
                  )
            ORDER BY id COLLATE NOCASE;
            """;
        AddSafeRangeParameters(command, bounds);

        var rows = ImmutableArray.CreateBuilder<AnalyticsTodoHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new AnalyticsTodoHistoryRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDateTimeOffset(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseDateOnly(reader.GetString(3)),
                (ReminderImportance)reader.GetInt32(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : ParseDateTimeOffset(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseDateTimeOffset(reader.GetString(7)));
            if (IsExactTodoCandidate(row, bounds))
                rows.Add(row);
        }
        return rows.ToImmutable();
    }

    private static async Task<ImmutableArray<AnalyticsReminderHistoryRow>> ReadRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalyticsRangeBounds bounds,
        bool includeDeleted,
        IReadOnlySet<Guid> actionOccurrenceIds,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidate_occurrences(id) AS (
                SELECT id
                FROM occurrences
                WHERE due_at_utc >= $startUtc AND due_at_utc < $endUtc
                UNION
                SELECT id
                FROM occurrences
                WHERE handled_at >= $safeStartText AND handled_at < $safeEndText
                UNION
                SELECT id
                FROM occurrences
                WHERE deleted_at >= $safeStartText AND deleted_at < $safeEndText
                UNION
                SELECT occurrence_id
                FROM action_log
                WHERE handled_at >= $safeStartText AND handled_at < $safeEndText
            )
            SELECT i.id, o.id, i.title, i.kind, i.importance, i.created_at,
                   o.due_at, o.state, o.handled_at, o.deleted_at
            FROM candidate_occurrences c
            INNER JOIN occurrences o ON o.id = c.id
            INNER JOIN items i ON i.id = o.item_id
            WHERE $includeDeleted = 1 OR o.deleted_at IS NULL
            ORDER BY o.due_at_utc, o.id COLLATE NOCASE;
            """;
        AddSafeRangeParameters(command, bounds);
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);

        var rows = ImmutableArray.CreateBuilder<AnalyticsReminderHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new AnalyticsReminderHistoryRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                (ReminderKind)reader.GetInt32(3),
                (ReminderImportance)reader.GetInt32(4),
                ParseDateTimeOffset(reader.GetString(5)),
                ParseDateTimeOffset(reader.GetString(6)),
                (OccurrenceState)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : ParseDateTimeOffset(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDateTimeOffset(reader.GetString(9)));
            if (IsWithin(row.DueAt, bounds) ||
                IsWithin(row.HandledAt, bounds) ||
                IsWithin(row.DeletedAt, bounds) ||
                actionOccurrenceIds.Contains(row.OccurrenceId))
            {
                rows.Add(row);
            }
        }
        return rows.ToImmutable();
    }

    private static async Task<ImmutableArray<AnalyticsActionHistoryRow>> ReadActionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalyticsRangeBounds bounds,
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
              AND a.handled_at >= $safeStartText
              AND a.handled_at < $safeEndText
            ORDER BY a.handled_at, a.id COLLATE NOCASE;
            """;
        AddSafeRangeParameters(command, bounds);
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);

        var rows = ImmutableArray.CreateBuilder<AnalyticsActionHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new AnalyticsActionHistoryRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                (OccurrenceState)reader.GetInt32(2),
                ParseDateTimeOffset(reader.GetString(3)));
            if (IsWithin(row.HandledAt, bounds))
                rows.Add(row);
        }
        return rows.ToImmutable();
    }

    private static bool IsExactTodoCandidate(
        AnalyticsTodoHistoryRow row,
        AnalyticsRangeBounds bounds)
    {
        if (row.DeletedAt is null && row.DueDate is null)
            return true;
        if (row.DueDate >= bounds.CandidateDateStart &&
            row.DueDate < bounds.CandidateDateEndExclusive)
        {
            return true;
        }
        return IsWithin(row.CompletedAt, bounds) || IsWithin(row.DeletedAt, bounds);
    }

    private static bool IsWithin(
        DateTimeOffset? value,
        AnalyticsRangeBounds bounds) =>
        value is not null && IsWithin(value.Value, bounds);

    private static bool IsWithin(
        DateTimeOffset value,
        AnalyticsRangeBounds bounds) =>
        value >= bounds.StartInclusive && value < bounds.EndExclusive;

    private static void AddSafeRangeParameters(
        SqliteCommand command,
        AnalyticsRangeBounds bounds)
    {
        command.Parameters.AddWithValue("$startUtc", bounds.StartUtcText);
        command.Parameters.AddWithValue("$endUtc", bounds.EndUtcText);
        command.Parameters.AddWithValue("$safeStartText", bounds.SafeStartText);
        command.Parameters.AddWithValue("$safeEndText", bounds.SafeEndTextExclusive);
        command.Parameters.AddWithValue(
            "$candidateDateStart", FormatDate(bounds.CandidateDateStart));
        command.Parameters.AddWithValue(
            "$candidateDateEnd", FormatDate(bounds.CandidateDateEndExclusive));
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateOnly ParseDateOnly(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record AnalyticsRangeBounds(
        DateTimeOffset StartInclusive,
        DateTimeOffset EndExclusive,
        string StartUtcText,
        string EndUtcText,
        string SafeStartText,
        string SafeEndTextExclusive,
        DateOnly CandidateDateStart,
        DateOnly CandidateDateEndExclusive)
    {
        public static AnalyticsRangeBounds Create(
            DateTimeOffset startInclusive,
            DateTimeOffset endExclusive)
        {
            startInclusive = startInclusive.ToUniversalTime();
            endExclusive = endExclusive.ToUniversalTime();
            var safeStartDate = DateOnly.FromDateTime(
                startInclusive.AddHours(-MaximumTimeZoneOffsetHours).UtcDateTime);
            var safeEndDateExclusive = DateOnly.FromDateTime(
                endExclusive.AddHours(MaximumTimeZoneOffsetHours).UtcDateTime).AddDays(1);
            return new AnalyticsRangeBounds(
                startInclusive,
                endExclusive,
                FormatUtc(startInclusive),
                FormatUtc(endExclusive),
                FormatDate(safeStartDate),
                FormatDate(safeEndDateExclusive),
                safeStartDate,
                safeEndDateExclusive);
        }
    }
}
