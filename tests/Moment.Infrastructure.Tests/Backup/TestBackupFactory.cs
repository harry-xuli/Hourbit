using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Moment.Infrastructure.Backup;
using Moment.Infrastructure.Data;

namespace Moment.Infrastructure.Tests.Backup;

internal static class TestBackupFactory
{
    internal static readonly TimeZoneInfo LocalZone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-backup-tests", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    internal static BackupService Create(
        string root,
        DateTimeOffset? now = null,
        IBackupRestoreLifecycle? lifecycle = null)
    {
        var current = now ?? DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        return new BackupService(
            DatabasePath(root),
            BackupDirectory(root),
            lifecycle,
            () => current,
            LocalZone);
    }

    internal static BackupService Create(
        string root,
        Func<DateTimeOffset> utcNow,
        IBackupRestoreLifecycle? lifecycle = null) =>
        new(
            DatabasePath(root),
            BackupDirectory(root),
            lifecycle,
            utcNow,
            LocalZone);

    internal static string DatabasePath(string root) =>
        Path.Combine(root, "moment.db");

    internal static string BackupDirectory(string root) =>
        Path.Combine(root, "backups");

    internal static async Task InitializeAsync(
        string root,
        string marker = "original")
    {
        var path = DatabasePath(root);
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);
        await SetMarkerAsync(connection, marker);
    }

    internal static async Task ChangeDatabaseAsync(
        string root,
        string marker = "changed")
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(DatabasePath(root), default);
        await SetMarkerAsync(connection, marker);
    }

    internal static async Task<string?> ReadMarkerAsync(string root)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(DatabasePath(root), default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'test_marker';";
        return (string?)await command.ExecuteScalarAsync();
    }

    internal static async Task TamperWithDatabaseEntryAsync(string path)
    {
        var entries = await ReadEntriesAsync(path);
        entries["moment.db"][0] ^= 0xff;
        await RewriteAsync(path, entries);
    }

    internal static async Task SetManifestSchemaVersionAsync(
        string path,
        int schemaVersion)
    {
        var entries = await ReadEntriesAsync(path);
        using var document = JsonDocument.Parse(entries["manifest.json"]);
        var root = document.RootElement;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = root.GetProperty("formatVersion").GetInt32(),
            schemaVersion,
            createdAt = root.GetProperty("createdAt").GetDateTimeOffset(),
            sha256 = root.GetProperty("sha256").GetString()
        });
        entries["manifest.json"] = json;
        await RewriteAsync(path, entries);
    }

    internal static async Task ReplaceWithUnmigratableDatabaseAsync(string path)
    {
        var databasePath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".unmigratable-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_info(version INTEGER NOT NULL);
                    INSERT INTO schema_info(version) VALUES (1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var entries = await ReadEntriesAsync(path);
            entries["moment.db"] = await File.ReadAllBytesAsync(databasePath);
            using var document = JsonDocument.Parse(entries["manifest.json"]);
            var root = document.RootElement;
            entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = root.GetProperty("formatVersion").GetInt32(),
                schemaVersion = 1,
                createdAt = root.GetProperty("createdAt").GetDateTimeOffset(),
                sha256 = Convert.ToHexStringLower(
                    SHA256.HashData(entries["moment.db"]))
            });
            await RewriteAsync(path, entries);
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    internal static async Task AddUnexpectedEntryAsync(string path)
    {
        var entries = await ReadEntriesAsync(path);
        entries["notes.txt"] = Encoding.UTF8.GetBytes("not allowed");
        await RewriteAsync(path, entries);
    }

    internal static async Task AddTraversalEntryAsync(string path)
    {
        var entries = await ReadEntriesAsync(path);
        entries["../moment.db"] = entries["moment.db"];
        await RewriteAsync(path, entries);
    }

    internal static async Task AddDuplicateDatabaseEntryAsync(string path)
    {
        var temp = path + ".rewrite";
        var entries = await ReadEntriesAsync(path);
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "moment.db", entries["moment.db"]);
            await WriteEntryAsync(archive, "moment.db", entries["moment.db"]);
            await WriteEntryAsync(archive, "manifest.json", entries["manifest.json"]);
        }
        File.Move(temp, path, overwrite: true);
    }

    internal static async Task DeclareDatabaseEntryLargerThanLimitAsync(
        string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        const uint declaredLength = 1024U * 1024U * 1024U + 1U;
        var patchedLocal = false;
        var patchedCentral = false;
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(index, 4));
            if (signature == 0x04034b50U)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(index + 26, 2));
                if (index + 30 + nameLength <= bytes.Length &&
                    Encoding.UTF8.GetString(
                        bytes, index + 30, nameLength) == "moment.db")
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(index + 22, 4), declaredLength);
                    patchedLocal = true;
                }
            }
            else if (signature == 0x02014b50U)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(index + 28, 2));
                if (index + 46 + nameLength <= bytes.Length &&
                    Encoding.UTF8.GetString(
                        bytes, index + 46, nameLength) == "moment.db")
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(index + 24, 4), declaredLength);
                    patchedCentral = true;
                }
            }
        }
        if (!patchedLocal || !patchedCentral)
            throw new InvalidOperationException("moment.db ZIP headers were not found.");
        await File.WriteAllBytesAsync(path, bytes);
    }

    internal static async Task<Dictionary<string, byte[]>> ReadEntriesAsync(
        string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            result[entry.FullName] = output.ToArray();
        }
        return result;
    }

    private static async Task RewriteAsync(
        string path,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        var temp = path + ".rewrite";
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var pair in entries)
                await WriteEntryAsync(archive, pair.Key, pair.Value);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        await using var output = entry.Open();
        await output.WriteAsync(bytes);
    }

    private static async Task SetMarkerAsync(
        SqliteConnection connection,
        string marker)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ('test_marker', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$value", marker);
        await command.ExecuteNonQueryAsync();
    }
}
