using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Domain;
using Moment.Core.Services;

namespace Moment.Infrastructure.Data;

public sealed class SqliteItemConversionStore : IItemConversionStore
{
    private readonly string _databasePath;

    private SqliteItemConversionStore(string databasePath) =>
        _databasePath = databasePath;

    public static async Task<SqliteItemConversionStore> OpenAsync(
        string databasePath,
        CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await DatabaseMigrator.MigrateAsync(connection, ct);
        return new SqliteItemConversionStore(databasePath);
    }

    public async Task<ItemConversionResult> ConvertTodoToReminderAsync(
        TodoToReminderConversion request,
        CancellationToken ct)
    {
        Validate(request);
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await SqliteReminderRepository.InsertItemAsync(
            connection, transaction, request.DestinationItem, ct);
        if (request.DestinationItem.Recurrence is not null)
        {
            await SqliteReminderRepository.InsertRecurrenceAsync(
                connection, transaction, request.DestinationItem.Id,
                request.DestinationItem.Recurrence, ct);
        }
        await SqliteReminderRepository.InsertOccurrenceAsync(
            connection, transaction, request.DestinationOccurrence, ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM todos WHERE id = $id;";
            delete.Parameters.AddWithValue(
                "$id", request.Source.Id.ToString("D"));
            if (await delete.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    "The todo conversion source no longer exists.");
            }
        }

        await transaction.CommitAsync(ct);
        return new ItemConversionResult(
            request.DestinationOccurrence.State == OccurrenceState.Scheduled);
    }

    public async Task<ItemConversionResult> ConvertReminderToTodoAsync(
        ReminderToTodoConversion request,
        CancellationToken ct)
    {
        Validate(request);
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await SqliteTodoRepository.InsertAsync(
            connection, transaction, request.Destination, ct);

        var persistedState = await GetAndValidateSourceAsync(
            connection, (SqliteTransaction)transaction, request.Source, ct);
        var continuationInserted = request.ContinuationOccurrence is not null &&
            await InsertContinuationIfMissingAsync(
                connection, (SqliteTransaction)transaction,
                request.ContinuationOccurrence, ct);

        bool schedulingChanged;
        switch (request.Scope)
        {
            case SeriesScope.OccurrenceOnly:
                await RemoveOccurrenceOnlyAsync(
                    connection, (SqliteTransaction)transaction,
                    request.Source, ct);
                schedulingChanged = persistedState is
                    OccurrenceState.Scheduled or OccurrenceState.Fired ||
                    continuationInserted;
                break;
            case SeriesScope.ThisAndFuture:
                schedulingChanged = await RemoveThisAndFutureAsync(
                    connection, (SqliteTransaction)transaction,
                    request.Source, persistedState, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        await DeleteItemWhenUnreferencedAsync(
            connection, (SqliteTransaction)transaction,
            request.Source.Item.Id, ct);
        await transaction.CommitAsync(ct);
        return new ItemConversionResult(schedulingChanged);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken ct) =>
        await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);

    private static async Task<OccurrenceState> GetAndValidateSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledReminder source,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_id, due_at_utc, state
            FROM occurrences
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$id", source.Occurrence.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "The reminder conversion source no longer exists.");
        }

        var itemId = Guid.Parse(reader.GetString(0));
        var dueAtUtc = reader.GetString(1);
        var state = (OccurrenceState)reader.GetInt32(2);
        if (itemId != source.Item.Id ||
            dueAtUtc != FormatUtc(source.Occurrence.DueAt) ||
            state != source.Occurrence.State)
        {
            throw new InvalidOperationException(
                "The reminder conversion source changed before conversion.");
        }
        return state;
    }

    private static async Task<bool> InsertContinuationIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReminderOccurrence occurrence,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO occurrences(
                id, item_id, due_at, due_at_utc, state,
                handled_at, snooze_parent_id)
            SELECT
                $id, $itemId, $dueAt, $dueAtUtc, $state,
                $handledAt, $snoozeParentId
            WHERE NOT EXISTS (
                SELECT 1
                FROM occurrences
                WHERE item_id = $itemId AND due_at_utc = $dueAtUtc
            );
            """;
        command.Parameters.AddWithValue("$id", occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$itemId", occurrence.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$dueAt", Format(occurrence.DueAt));
        command.Parameters.AddWithValue(
            "$dueAtUtc", FormatUtc(occurrence.DueAt));
        command.Parameters.AddWithValue("$state", (int)occurrence.State);
        command.Parameters.AddWithValue("$handledAt",
            occurrence.HandledAt is null
                ? DBNull.Value
                : Format(occurrence.HandledAt.Value));
        command.Parameters.AddWithValue("$snoozeParentId",
            occurrence.SnoozeParentId is null
                ? DBNull.Value
                : occurrence.SnoozeParentId.Value.ToString("D"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task RemoveOccurrenceOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledReminder source,
        CancellationToken ct)
    {
        await ClearSnoozeParentAsync(
            connection, transaction, source.Occurrence.Id, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM occurrences
            WHERE id = $id AND item_id = $itemId;
            """;
        command.Parameters.AddWithValue(
            "$id", source.Occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$itemId", source.Item.Id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "The reminder conversion source no longer exists.");
        }
    }

    private static async Task<bool> RemoveThisAndFutureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledReminder source,
        OccurrenceState persistedState,
        CancellationToken ct)
    {
        var scheduledCount = await CountScheduledTailAsync(
            connection, transaction, source, ct);
        await ClearDeletedTailSnoozeParentsAsync(
            connection, transaction, source, ct);

        await using (var recurrence = connection.CreateCommand())
        {
            recurrence.Transaction = transaction;
            recurrence.CommandText =
                "DELETE FROM recurrence_rules WHERE item_id = $itemId;";
            recurrence.Parameters.AddWithValue(
                "$itemId", source.Item.Id.ToString("D"));
            await recurrence.ExecuteNonQueryAsync(ct);
        }

        await using (var occurrences = connection.CreateCommand())
        {
            occurrences.Transaction = transaction;
            occurrences.CommandText = """
                DELETE FROM occurrences
                WHERE item_id = $itemId
                  AND (
                      id = $sourceId OR
                      (state = $scheduled AND due_at_utc >= $cutoffUtc)
                  );
                """;
            occurrences.Parameters.AddWithValue(
                "$itemId", source.Item.Id.ToString("D"));
            occurrences.Parameters.AddWithValue(
                "$sourceId", source.Occurrence.Id.ToString("D"));
            occurrences.Parameters.AddWithValue(
                "$scheduled", (int)OccurrenceState.Scheduled);
            occurrences.Parameters.AddWithValue(
                "$cutoffUtc", FormatUtc(source.Occurrence.DueAt));
            if (await occurrences.ExecuteNonQueryAsync(ct) < 1)
            {
                throw new InvalidOperationException(
                    "The reminder conversion source no longer exists.");
            }
        }

        return scheduledCount > 0 || persistedState == OccurrenceState.Fired;
    }

    private static async Task<int> CountScheduledTailAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledReminder source,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM occurrences
            WHERE item_id = $itemId
              AND state = $scheduled
              AND due_at_utc >= $cutoffUtc;
            """;
        command.Parameters.AddWithValue(
            "$itemId", source.Item.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue(
            "$cutoffUtc", FormatUtc(source.Occurrence.DueAt));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static async Task ClearSnoozeParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceOccurrenceId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE occurrences
            SET snooze_parent_id = NULL
            WHERE snooze_parent_id = $sourceId;
            """;
        command.Parameters.AddWithValue(
            "$sourceId", sourceOccurrenceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ClearDeletedTailSnoozeParentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledReminder source,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE occurrences
            SET snooze_parent_id = NULL
            WHERE snooze_parent_id IN (
                SELECT id
                FROM occurrences
                WHERE item_id = $itemId
                  AND (
                      id = $sourceId OR
                      (state = $scheduled AND due_at_utc >= $cutoffUtc)
                  )
            );
            """;
        command.Parameters.AddWithValue(
            "$itemId", source.Item.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$sourceId", source.Occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue(
            "$cutoffUtc", FormatUtc(source.Occurrence.DueAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteItemWhenUnreferencedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM items
            WHERE id = $itemId
              AND NOT EXISTS (
                  SELECT 1 FROM occurrences WHERE item_id = $itemId
              );
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Validate(TodoToReminderConversion request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DestinationOccurrence.ItemId !=
                request.DestinationItem.Id ||
            request.DestinationItem.CreatedAt != request.Source.CreatedAt ||
            request.DestinationOccurrence.State !=
                (request.Source.IsCompleted
                    ? OccurrenceState.Completed
                    : OccurrenceState.Scheduled) ||
            request.DestinationOccurrence.HandledAt !=
                request.Source.CompletedAt)
        {
            throw new ArgumentException(
                "Todo conversion state mapping is inconsistent.",
                nameof(request));
        }
    }

    private static void Validate(ReminderToTodoConversion request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isCompleted =
            request.Source.Occurrence.State == OccurrenceState.Completed;
        var requiresContinuation =
            request.Scope == SeriesScope.OccurrenceOnly &&
            request.Source.Item.Recurrence is not null &&
            request.Source.Occurrence.State is
                OccurrenceState.Scheduled or OccurrenceState.Fired;
        if (!Enum.IsDefined(request.Scope) ||
            request.Source.Occurrence.ItemId != request.Source.Item.Id ||
            request.Destination.CreatedAt != request.Source.Item.CreatedAt ||
            request.Destination.IsCompleted != isCompleted ||
            request.Destination.CompletedAt !=
                (isCompleted ? request.Source.Occurrence.HandledAt : null) ||
            requiresContinuation !=
                (request.ContinuationOccurrence is not null) ||
            request.ContinuationOccurrence is not null &&
                (request.Scope != SeriesScope.OccurrenceOnly ||
                 request.Source.Item.Recurrence is null ||
                 request.Source.Occurrence.State is not
                    (OccurrenceState.Scheduled or OccurrenceState.Fired) ||
                 request.ContinuationOccurrence.ItemId !=
                    request.Source.Item.Id ||
                 request.ContinuationOccurrence.State !=
                    OccurrenceState.Scheduled ||
                 request.ContinuationOccurrence.HandledAt is not null ||
                 request.ContinuationOccurrence.SnoozeParentId is not null ||
                 request.ContinuationOccurrence.DueAt <=
                    request.Source.Occurrence.DueAt))
        {
            throw new ArgumentException(
                "Reminder conversion state mapping is inconsistent.",
                nameof(request));
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
