using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Abstractions;
using Moment.Core.Domain;

namespace Moment.Infrastructure.Data;

public sealed class SqliteReminderRepository : IReminderRepository
{
    private readonly string _databasePath;

    private SqliteReminderRepository(string databasePath) => _databasePath = databasePath;

    public static async Task<SqliteReminderRepository> OpenAsync(string databasePath, CancellationToken ct)
    {
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await DatabaseMigrator.MigrateAsync(connection, ct);
        return new SqliteReminderRepository(databasePath);
    }

    public async Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InsertItemAsync(connection, transaction, item, ct);
        if (item.Recurrence is not null)
        {
            await InsertRecurrenceAsync(connection, transaction, item.Id, item.Recurrence, ct);
        }

        await InsertOccurrenceAsync(connection, transaction, occurrence, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ScheduledReminder>> GetScheduledAsync(CancellationToken ct) =>
        await GetScheduledRemindersAsync("o.state = $state", command =>
        {
            command.Parameters.AddWithValue("$state", (int)OccurrenceState.Scheduled);
        }, ct);

    public async Task<IReadOnlyList<ScheduledReminder>> GetDueAsync(DateTimeOffset through, CancellationToken ct) =>
        await GetScheduledRemindersAsync("o.state = $state AND o.due_at_utc <= $throughUtc", command =>
        {
            command.Parameters.AddWithValue("$state", (int)OccurrenceState.Scheduled);
            command.Parameters.AddWithValue("$throughUtc", FormatUtcKey(through));
        }, ct);

    public async Task<IReadOnlyList<ScheduledReminder>> GetRecoverableAsync(DateTimeOffset through, CancellationToken ct) =>
        await GetScheduledRemindersAsync("""
            (o.state = $scheduled AND o.due_at_utc <= $throughUtc)
            OR (o.state = $fired AND i.importance = $normal)
            OR (o.state = $deliveryFailed AND o.next_delivery_attempt_at IS NOT NULL)
            OR (o.state = $delivering)
            """, command =>
        {
            command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
            command.Parameters.AddWithValue("$throughUtc", FormatUtcKey(through));
            command.Parameters.AddWithValue("$fired", (int)OccurrenceState.Fired);
            command.Parameters.AddWithValue("$normal", (int)ReminderImportance.Normal);
            command.Parameters.AddWithValue("$deliveryFailed", (int)OccurrenceState.DeliveryFailed);
            command.Parameters.AddWithValue("$delivering", (int)OccurrenceState.Delivering);
        }, ct);

    public async Task<ScheduledReminder?> GetScheduledReminderAsync(Guid occurrenceId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = CreateReminderQuery(connection, "o.id = $id");
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadScheduledReminder(reader) : null;
    }

    public async Task<ReminderItem?> GetItemAsync(Guid itemId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.title, i.kind, i.importance, i.created_at,
                   r.kind, r.days_of_week, r.time
            FROM items i
            LEFT JOIN recurrence_rules r ON r.item_id = i.id
            WHERE i.id = $id
              AND EXISTS (
                  SELECT 1
                  FROM occurrences o
                  WHERE o.item_id = i.id AND o.deleted_at IS NULL
              );
            """;
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    public async Task SetOccurrenceStateAsync(Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await UpdateOccurrenceStateAsync(connection, null, occurrenceId, state, handledAt, ct);
    }

    public async Task SaveOccurrenceAsync(ReminderOccurrence occurrence, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await InsertOccurrenceAsync(connection, null, occurrence, ct);
    }

    public async Task<bool> TryMarkFiredAsync(Guid occurrenceId, DateTimeOffset firedAt, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET state = $fired, handled_at = $firedAt
            WHERE id = $id
              AND state = $scheduled
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$fired", (int)OccurrenceState.Fired);
        command.Parameters.AddWithValue("$firedAt", Format(firedAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TryBeginDeliveryAsync(
        Guid occurrenceId,
        DateTimeOffset attemptedAt,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET state = $delivering,
                handled_at = $attemptedAt,
                delivery_attempts = delivery_attempts + 1,
                last_delivery_error = NULL,
                next_delivery_attempt_at = NULL
            WHERE id = $id
              AND state = $scheduled
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$delivering", (int)OccurrenceState.Delivering);
        command.Parameters.AddWithValue("$attemptedAt", Format(attemptedAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task CompleteDeliveryAsync(
        Guid occurrenceId,
        DateTimeOffset firedAt,
        ReminderOccurrence? nextOccurrence,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE occurrences
            SET state = $fired,
                handled_at = $firedAt,
                last_delivery_error = NULL,
                next_delivery_attempt_at = NULL
            WHERE id = $id
              AND state = $delivering
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$fired", (int)OccurrenceState.Fired);
        command.Parameters.AddWithValue("$firedAt", Format(firedAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$delivering", (int)OccurrenceState.Delivering);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        if (nextOccurrence is not null)
            await InsertOccurrenceAsync(connection, transaction, nextOccurrence, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecordDeliveryFailureAsync(
        Guid occurrenceId,
        DateTimeOffset attemptedAt,
        string errorCode,
        DateTimeOffset? retryAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET state = $failed,
                handled_at = $attemptedAt,
                last_delivery_error = $errorCode,
                next_delivery_attempt_at = $retryAt
            WHERE id = $id
              AND state = $delivering
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$failed", (int)OccurrenceState.DeliveryFailed);
        command.Parameters.AddWithValue("$attemptedAt", Format(attemptedAt));
        command.Parameters.AddWithValue("$errorCode", errorCode);
        command.Parameters.AddWithValue("$retryAt", retryAt is null ? DBNull.Value : Format(retryAt.Value));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$delivering", (int)OccurrenceState.Delivering);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RetryDeliveryAsync(
        Guid occurrenceId,
        DateTimeOffset retryAt,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET state = $scheduled,
                handled_at = NULL,
                next_delivery_attempt_at = NULL
            WHERE id = $id
              AND state = $failed
              AND next_delivery_attempt_at IS NOT NULL
              AND next_delivery_attempt_at <= $retryAt
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$failed", (int)OccurrenceState.DeliveryFailed);
        command.Parameters.AddWithValue("$retryAt", Format(retryAt));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TryTransitionAsync(Guid occurrenceId, OccurrenceState expected,
        OccurrenceState next, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET state = $next, handled_at = $handledAt
            WHERE id = $id
              AND state = $expected
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$next", (int)next);
        command.Parameters.AddWithValue("$handledAt", Format(handledAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$expected", (int)expected);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state,
        DateTimeOffset handledAt, ReminderOccurrence? nextOccurrence, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await TryApplyActionStateAsync(connection, transaction, occurrenceId, state, handledAt, ct))
        {
            await transaction.CommitAsync(ct);
            return;
        }

        if (nextOccurrence is not null)
        {
            await InsertOccurrenceAsync(connection, transaction, nextOccurrence, ct);
        }

        await InsertActionLogAsync(connection, transaction, occurrenceId, state, handledAt, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task EditAsync(Guid occurrenceId, ReminderItem item,
        ReminderOccurrence occurrence, SeriesScope scope, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await GetOccurrenceContextAsync(connection, transaction, occurrenceId, ct);
        if (current is null)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        if (scope == SeriesScope.ThisAndFuture)
        {
            var futureItem = item with { Id = Guid.NewGuid() };
            await InsertItemAsync(connection, transaction, futureItem, ct);
            if (futureItem.Recurrence is not null)
            {
                await InsertRecurrenceAsync(connection, transaction, futureItem.Id, futureItem.Recurrence, ct);
            }

            await DeleteScheduledOccurrencesAtOrAfterAsync(connection, transaction,
                current.Value.ItemId, current.Value.DueAt, ct);
            await InsertOccurrenceAsync(connection, transaction, occurrence with { ItemId = futureItem.Id }, ct);
        }
        else
        {
            var singleItem = item with { Id = Guid.NewGuid(), Recurrence = null };
            await InsertItemAsync(connection, transaction, singleItem, ct);
            await UpdateOccurrenceAsync(connection, transaction, occurrenceId,
                occurrence with { ItemId = singleItem.Id }, ct);
        }

        await transaction.CommitAsync(ct);
    }

    public Task DeleteAsync(
        Guid occurrenceId,
        SeriesScope scope,
        CancellationToken ct) =>
        DeleteAsync(occurrenceId, scope, DateTimeOffset.UtcNow, ct);

    public async Task DeleteAsync(
        Guid occurrenceId,
        SeriesScope scope,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (scope == SeriesScope.OccurrenceOnly)
        {
            await SoftDeleteOccurrenceAsync(
                connection, transaction, occurrenceId, deletedAt, ct);
        }
        else
        {
            var occurrence = await GetOccurrenceContextAsync(connection, transaction, occurrenceId, ct);
            if (occurrence is not null)
            {
                await DeleteRecurrenceAsync(connection, transaction, occurrence.Value.ItemId, ct);
                await SoftDeleteScheduledOccurrencesAtOrAfterAsync(
                    connection, transaction, occurrence.Value.ItemId,
                    occurrence.Value.DueAt, deletedAt, ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct) =>
        await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);

    private async Task<IReadOnlyList<ScheduledReminder>> GetScheduledRemindersAsync(
        string predicate, Action<SqliteCommand> configure, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = CreateReminderQuery(connection, predicate);
        configure(command);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var reminders = new List<ScheduledReminder>();
        while (await reader.ReadAsync(ct))
        {
            reminders.Add(ReadScheduledReminder(reader));
        }

        return reminders;
    }

    private static SqliteCommand CreateReminderQuery(SqliteConnection connection, string predicate)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id, i.title, i.kind, i.importance, i.created_at,
                   r.kind, r.days_of_week, r.time,
                   o.id, o.item_id, o.due_at, o.state, o.handled_at, o.snooze_parent_id,
                   o.delivery_attempts, o.last_delivery_error, o.next_delivery_attempt_at
            FROM occurrences o
            INNER JOIN items i ON i.id = o.item_id
            LEFT JOIN recurrence_rules r ON r.item_id = i.id
            WHERE o.deleted_at IS NULL AND ({predicate})
            ORDER BY o.due_at_utc, o.id;
            """;
        return command;
    }

    private static ScheduledReminder ReadScheduledReminder(SqliteDataReader reader) =>
        new(ReadItem(reader), new ReminderOccurrence(
            ParseGuid(reader.GetString(8)),
            ParseGuid(reader.GetString(9)),
            ParseDateTimeOffset(reader.GetString(10)),
            (OccurrenceState)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : ParseDateTimeOffset(reader.GetString(12)),
            reader.IsDBNull(13) ? null : ParseGuid(reader.GetString(13)),
            reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : ParseDateTimeOffset(reader.GetString(16))));

    private static ReminderItem ReadItem(SqliteDataReader reader)
    {
        RecurrenceRule? recurrence = null;
        if (!reader.IsDBNull(5))
        {
            var kind = (RecurrenceKind)reader.GetInt32(5);
            var time = TimeOnly.ParseExact(reader.GetString(7), "O", CultureInfo.InvariantCulture);
            recurrence = kind switch
            {
                RecurrenceKind.Daily => RecurrenceRule.Daily(time),
                RecurrenceKind.Weekdays => RecurrenceRule.Weekdays(time),
                RecurrenceKind.Weekly => RecurrenceRule.Weekly(
                    reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(static day => (DayOfWeek)int.Parse(day, CultureInfo.InvariantCulture)), time),
                _ => throw new InvalidOperationException("Unknown recurrence kind.")
            };
        }

        return new ReminderItem(
            ParseGuid(reader.GetString(0)),
            reader.GetString(1),
            (ReminderKind)reader.GetInt32(2),
            (ReminderImportance)reader.GetInt32(3),
            ParseDateTimeOffset(reader.GetString(4)),
            recurrence);
    }

    internal static async Task InsertItemAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction,
        ReminderItem item, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = "INSERT INTO items(id, title, kind, importance, created_at) VALUES ($id, $title, $kind, $importance, $createdAt);";
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$importance", (int)item.Importance);
        command.Parameters.AddWithValue("$createdAt", Format(item.CreatedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertRecurrenceAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction,
        Guid itemId, RecurrenceRule rule, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = "INSERT INTO recurrence_rules(item_id, kind, days_of_week, time) VALUES ($itemId, $kind, $days, $time);";
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)rule.Kind);
        command.Parameters.AddWithValue("$days", FormatDays(rule.DaysOfWeek));
        command.Parameters.AddWithValue("$time", rule.Time.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertOccurrenceAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction,
        ReminderOccurrence occurrence, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            INSERT INTO occurrences(
                id, item_id, due_at, due_at_utc, state, handled_at,
                snooze_parent_id, delivery_attempts, last_delivery_error,
                next_delivery_attempt_at)
            VALUES (
                $id, $itemId, $dueAt, $dueAtUtc, $state, $handledAt,
                $snoozeParentId, $deliveryAttempts, $lastDeliveryError,
                $nextDeliveryAttemptAt);
            """;
        command.Parameters.AddWithValue("$id", occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue("$itemId", occurrence.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$dueAt", Format(occurrence.DueAt));
        command.Parameters.AddWithValue("$dueAtUtc", FormatUtcKey(occurrence.DueAt));
        command.Parameters.AddWithValue("$state", (int)occurrence.State);
        command.Parameters.AddWithValue("$handledAt", occurrence.HandledAt is null ? DBNull.Value : Format(occurrence.HandledAt.Value));
        command.Parameters.AddWithValue("$snoozeParentId", occurrence.SnoozeParentId is null ? DBNull.Value : occurrence.SnoozeParentId.Value.ToString("D"));
        command.Parameters.AddWithValue("$deliveryAttempts", occurrence.DeliveryAttempts);
        command.Parameters.AddWithValue("$lastDeliveryError", occurrence.LastDeliveryError is null ? DBNull.Value : occurrence.LastDeliveryError);
        command.Parameters.AddWithValue("$nextDeliveryAttemptAt", occurrence.NextDeliveryAttemptAt is null ? DBNull.Value : Format(occurrence.NextDeliveryAttemptAt.Value));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateOccurrenceStateAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction,
        Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = "UPDATE occurrences SET state = $state, handled_at = $handledAt WHERE id = $id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$handledAt", Format(handledAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TryApplyActionStateAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE occurrences
            SET state = $state, handled_at = $handledAt
            WHERE id = $id
              AND state IN ($scheduled, $fired)
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$handledAt", Format(handledAt));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue("$fired", (int)OccurrenceState.Fired);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task InsertActionLogAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO action_log(id, occurrence_id, state, handled_at) VALUES ($id, $occurrenceId, $state, $handledAt);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$occurrenceId", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$handledAt", Format(handledAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateItemAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        ReminderItem item, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE items SET title = $title, kind = $kind, importance = $importance, created_at = $createdAt WHERE id = $id;";
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$importance", (int)item.Importance);
        command.Parameters.AddWithValue("$createdAt", Format(item.CreatedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertRecurrenceAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        ReminderItem item, CancellationToken ct)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM recurrence_rules WHERE item_id = $itemId;";
        delete.Parameters.AddWithValue("$itemId", item.Id.ToString("D"));
        await delete.ExecuteNonQueryAsync(ct);
        if (item.Recurrence is not null)
        {
            await InsertRecurrenceAsync(connection, transaction, item.Id, item.Recurrence, ct);
        }
    }

    private static async Task UpdateOccurrenceAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid occurrenceId, ReminderOccurrence occurrence, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE occurrences
            SET id = $newId, item_id = $itemId, due_at = $dueAt, state = $state,
                due_at_utc = $dueAtUtc, handled_at = $handledAt,
                snooze_parent_id = $snoozeParentId,
                delivery_attempts = $deliveryAttempts,
                last_delivery_error = $lastDeliveryError,
                next_delivery_attempt_at = $nextDeliveryAttemptAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$newId", occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue("$itemId", occurrence.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$dueAt", Format(occurrence.DueAt));
        command.Parameters.AddWithValue("$dueAtUtc", FormatUtcKey(occurrence.DueAt));
        command.Parameters.AddWithValue("$state", (int)occurrence.State);
        command.Parameters.AddWithValue("$handledAt", occurrence.HandledAt is null ? DBNull.Value : Format(occurrence.HandledAt.Value));
        command.Parameters.AddWithValue("$snoozeParentId", occurrence.SnoozeParentId is null ? DBNull.Value : occurrence.SnoozeParentId.Value.ToString("D"));
        command.Parameters.AddWithValue("$deliveryAttempts", occurrence.DeliveryAttempts);
        command.Parameters.AddWithValue("$lastDeliveryError", occurrence.LastDeliveryError is null ? DBNull.Value : occurrence.LastDeliveryError);
        command.Parameters.AddWithValue("$nextDeliveryAttemptAt", occurrence.NextDeliveryAttemptAt is null ? DBNull.Value : Format(occurrence.NextDeliveryAttemptAt.Value));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SoftDeleteOccurrenceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid occurrenceId,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE occurrences
            SET deleted_at = $deletedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$deletedAt", Format(deletedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(Guid ItemId, DateTimeOffset DueAt)?> GetOccurrenceContextAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid occurrenceId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT item_id, due_at FROM occurrences WHERE id = $id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (ParseGuid(reader.GetString(0)), ParseDateTimeOffset(reader.GetString(1)))
            : null;
    }

    private static async Task DeleteRecurrenceAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid itemId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM recurrence_rules WHERE item_id = $id;";
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteScheduledOccurrencesAtOrAfterAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid itemId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM occurrences
            WHERE item_id = $itemId
              AND state = $scheduled
              AND due_at_utc >= $cutoffUtc
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        command.Parameters.AddWithValue("$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue("$cutoffUtc", FormatUtcKey(cutoff));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SoftDeleteScheduledOccurrencesAtOrAfterAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid itemId,
        DateTimeOffset cutoff,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE occurrences
            SET deleted_at = $deletedAt
            WHERE item_id = $itemId
              AND state = $scheduled
              AND due_at_utc >= $cutoffUtc
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        command.Parameters.AddWithValue(
            "$scheduled", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue("$cutoffUtc", FormatUtcKey(cutoff));
        command.Parameters.AddWithValue("$deletedAt", Format(deletedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatUtcKey(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDays(IEnumerable<DayOfWeek> days) => string.Join(',', days.OrderBy(static day => day).Select(static day => ((int)day).ToString(CultureInfo.InvariantCulture)));

    private static Guid ParseGuid(string value) => Guid.Parse(value);

    private static DateTimeOffset ParseDateTimeOffset(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
