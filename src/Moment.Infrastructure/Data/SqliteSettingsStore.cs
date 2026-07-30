using System.Globalization;
using Microsoft.Data.Sqlite;
using Moment.Core.Abstractions;

namespace Moment.Infrastructure.Data;

public sealed class SqliteSettingsStore(string databasePath) : ISettingsStore
{
    private const string HotkeyKey = "hotkey";
    private const string StartWithWindowsKey = "start_with_windows";
    private const string AlertVolumeKey = "alert_volume";
    private const string CustomAlertSoundPathKey = "custom_alert_sound_path";

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value
            FROM settings
            WHERE key IN ($hotkey, $startup, $volume, $sound);
            """;
        command.Parameters.AddWithValue("$hotkey", HotkeyKey);
        command.Parameters.AddWithValue("$startup", StartWithWindowsKey);
        command.Parameters.AddWithValue("$volume", AlertVolumeKey);
        command.Parameters.AddWithValue("$sound", CustomAlertSoundPathKey);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values[reader.GetString(0)] = reader.GetString(1);

        var hotkey = values.GetValueOrDefault(HotkeyKey);
        if (string.IsNullOrWhiteSpace(hotkey))
            hotkey = "Ctrl+Alt+Space";

        var startup = values.TryGetValue(StartWithWindowsKey, out var startupText) &&
                      bool.TryParse(startupText, out var enabled) &&
                      enabled;
        var volume = values.TryGetValue(AlertVolumeKey, out var volumeText) &&
                     int.TryParse(volumeText, NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out var parsedVolume)
            ? Math.Clamp(parsedVolume, 0, 100)
            : 100;
        var soundPath = values.GetValueOrDefault(CustomAlertSoundPathKey);
        if (string.IsNullOrWhiteSpace(soundPath))
            soundPath = null;

        return new AppSettings(hotkey, startup, volume, soundPath);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(databasePath, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await UpsertAsync(connection, (SqliteTransaction)transaction,
            HotkeyKey, settings.Hotkey, ct);
        await UpsertAsync(connection, (SqliteTransaction)transaction,
            StartWithWindowsKey, settings.StartWithWindows.ToString(), ct);
        await UpsertAsync(connection, (SqliteTransaction)transaction,
            AlertVolumeKey,
            Math.Clamp(settings.AlertVolume, 0, 100)
                .ToString(CultureInfo.InvariantCulture), ct);

        if (string.IsNullOrWhiteSpace(settings.CustomAlertSoundPath))
        {
            await DeleteAsync(connection, (SqliteTransaction)transaction,
                CustomAlertSoundPathKey, ct);
        }
        else
        {
            await UpsertAsync(connection, (SqliteTransaction)transaction,
                CustomAlertSoundPathKey, settings.CustomAlertSoundPath, ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct);
    }
}
