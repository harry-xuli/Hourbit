using System.Globalization;
using Microsoft.Data.Sqlite;
using Hourbit.Core.Domain;
using Hourbit.Core.Search;

namespace Hourbit.Infrastructure.Data;

public sealed class SqliteItemSearchQuery(string databasePath) : IItemSearchQuery
{
    private readonly string _databasePath = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("A database path is required.", nameof(databasePath))
        : databasePath;

    public async Task<IReadOnlyList<ItemSearchResult>> SearchAsync(
        ItemSearchFilter filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ct.ThrowIfCancellationRequested();
        if (filter.Text.Length == 0)
            return [];

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            _databasePath, ct, SqliteCacheMode.Private);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, item_type, title, local_date, importance, is_completed
            FROM (
                SELECT o.id AS id,
                       0 AS item_type,
                       i.title AS title,
                       substr(o.due_at, 1, 10) AS local_date,
                       i.importance AS importance,
                       CASE WHEN o.state = 1 THEN 1 ELSE 0 END AS is_completed
                FROM occurrences o
                INNER JOIN items i ON i.id = o.item_id
                WHERE o.deleted_at IS NULL
                  AND instr(lower(i.title), lower($text)) > 0

                UNION ALL

                SELECT t.id AS id,
                       1 AS item_type,
                       t.title AS title,
                       t.due_date AS local_date,
                       t.importance AS importance,
                       t.is_completed AS is_completed
                FROM todos t
                WHERE t.deleted_at IS NULL
                  AND instr(lower(t.title), lower($text)) > 0
            )
            ORDER BY local_date IS NULL,
                     local_date,
                     item_type,
                     title COLLATE NOCASE,
                     id COLLATE NOCASE
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$text", filter.Text);

        var rows = new List<ItemSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ItemSearchResult(
                Guid.Parse(reader.GetString(0)),
                (SearchItemType)reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3)
                    ? null
                    : DateOnly.ParseExact(
                        reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (ReminderImportance)reader.GetInt32(4),
                reader.GetInt32(5) == 1));
        }

        await transaction.CommitAsync(ct);
        return rows;
    }
}
