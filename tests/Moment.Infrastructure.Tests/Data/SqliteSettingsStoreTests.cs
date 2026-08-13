using Microsoft.Data.Sqlite;
using Moment.Core.Abstractions;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

public sealed class SqliteSettingsStoreTests
{
    [Fact]
    public async Task Empty_settings_table_loads_product_defaults()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "moment.db");
        await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var store = new SqliteSettingsStore(path);

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(new AppSettings("Ctrl+Alt+Space", false, 100, null, null), settings);
    }

    [Fact]
    public async Task Save_round_trips_all_settings_without_removing_unrelated_rows()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "moment.db");
        await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        await InsertAsync(path, "future-setting", "keep");
        var store = new SqliteSettingsStore(path);
        var expected = new AppSettings(
            "Ctrl+Shift+R", true, 37, @"C:\Sounds\alert.wav", "en-US");

        await store.SaveAsync(expected, CancellationToken.None);
        var actual = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal("keep", await ReadAsync(path, "future-setting"));
    }

    private static async Task InsertAsync(string path, string key, string value)
    {
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings(key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadAsync(string path, string key)
    {
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }
}
