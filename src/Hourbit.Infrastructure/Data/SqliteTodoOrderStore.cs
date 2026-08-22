using System.Text.Json;
using Hourbit.Core.Abstractions;

namespace Hourbit.Infrastructure.Data;

public sealed class SqliteTodoOrderStore(string databasePath) : ITodoOrderStore
{
    private const string SettingKey = "todo_display_order";

    public async Task<IReadOnlyList<Guid>> LoadAsync(CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SettingKey);
        var stored = await command.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<Guid[]>(stored) ?? [])
                .Distinct()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<Guid> todoIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(todoIds);
        var value = JsonSerializer.Serialize(todoIds.Distinct().ToArray());
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", SettingKey);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
