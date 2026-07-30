using System.IO.Compression;
using System.Text.Json;
using Moment.Infrastructure.Backup;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Backup;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task Export_and_restore_round_trips_and_rejects_tampering()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var service = TestBackupFactory.Create(temp.Path);
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await service.ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path);

        await service.RestoreAsync(path, default);

        Assert.Equal("original", await TestBackupFactory.ReadMarkerAsync(temp.Path));

        await TestBackupFactory.TamperWithDatabaseEntryAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.RestoreAsync(path, default));
    }

    [Fact]
    public async Task Export_contains_only_database_and_utf8_manifest()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "data.moment-backup");

        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);

        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(
            ["manifest.json", "moment.db"],
            archive.Entries.Select(entry => entry.FullName).Order().ToArray());
        var manifestEntry = Assert.Single(
            archive.Entries, entry => entry.FullName == "manifest.json");
        await using var stream = manifestEntry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-29T01:02:03Z"),
            document.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Matches(
            "^[0-9a-f]{64}$",
            document.RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Automatic_backups_keep_the_seven_newest_successful_packages()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-20T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);

        for (var index = 0; index < 9; index++)
        {
            now = now.AddDays(1);
            await service.CreateDailyBackupAsync(default);
        }

        var names = Directory.GetFiles(
                TestBackupFactory.BackupDirectory(temp.Path),
                "*.moment-backup")
            .Select(Path.GetFileName)
            .Order()
            .ToArray();
        Assert.Equal(7, names.Length);
        Assert.Equal("moment-20260723T010203000Z.moment-backup", names[0]);
        Assert.Equal("moment-20260729T010203000Z.moment-backup", names[^1]);
    }

    [Fact]
    public async Task Daily_backup_skips_a_second_package_on_the_same_local_date()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);
        var first = await service.CreateDailyBackupAsync(default);
        now = DateTimeOffset.Parse("2026-07-29T15:59:59Z");

        var second = await service.CreateDailyBackupAsync(default);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(
            TestBackupFactory.BackupDirectory(temp.Path),
            "*.moment-backup"));
    }

    [Fact]
    public async Task Restore_rejects_unsafe_or_incompatible_packages_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var valid = Path.Combine(temp.Path, "valid.moment-backup");
        var service = TestBackupFactory.Create(temp.Path);
        await service.ExportAsync(valid, default);
        var mutations = new Func<string, Task>[]
        {
            TestBackupFactory.AddUnexpectedEntryAsync,
            TestBackupFactory.AddTraversalEntryAsync,
            TestBackupFactory.AddDuplicateDatabaseEntryAsync,
            path => TestBackupFactory.SetManifestSchemaVersionAsync(path, 999)
        };

        foreach (var mutate in mutations)
        {
            var path = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.moment-backup");
            File.Copy(valid, path);
            await mutate(path);
            var lifecycle = new RecordingLifecycle();
            var guarded = TestBackupFactory.Create(temp.Path, lifecycle: lifecycle);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => guarded.RestoreAsync(path, default));

            Assert.Empty(lifecycle.Events);
        }

        var corrupt = Path.Combine(temp.Path, "corrupt.moment-backup");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4]);
        var corruptLifecycle = new RecordingLifecycle();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: corruptLifecycle).RestoreAsync(corrupt, default));
        Assert.Empty(corruptLifecycle.Events);
    }

    [Fact]
    public async Task Restore_rejects_database_declared_over_one_gib_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "oversized.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.DeclareDatabaseEntryLargerThanLimitAsync(path);
        var lifecycle = new RecordingLifecycle();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: lifecycle).RestoreAsync(path, default));

        Assert.Contains("1 GiB", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Events);
    }

    [Fact]
    public async Task Failed_export_preserves_existing_destination_and_cleans_staging_files()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "existing.moment-backup");
        var existingBytes = new byte[] { 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(destination, existingBytes);
        await File.WriteAllBytesAsync(
            TestBackupFactory.DatabasePath(temp.Path),
            [1, 2, 3, 4]);

        await Assert.ThrowsAnyAsync<Exception>(
            () => TestBackupFactory.Create(temp.Path)
                .ExportAsync(destination, default));

        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(
            Directory.GetFiles(temp.Path, ".moment-*.tmp"));
        Assert.Empty(
            Directory.GetFiles(temp.Path, ".moment-*.snapshot.db"));
    }

    [Fact]
    public async Task Restore_rolls_back_database_and_restarts_when_refresh_fails()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "backup");
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "current");
        var lifecycle = new RecordingLifecycle { FailFirstRefresh = true };
        var service = TestBackupFactory.Create(temp.Path, lifecycle: lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestoreAsync(path, default));

        Assert.Equal("current", await TestBackupFactory.ReadMarkerAsync(temp.Path));
        Assert.Equal(
            ["stop", "start", "refresh", "start", "refresh"],
            lifecycle.Events);
    }

    [Fact]
    public async Task Export_is_a_consistent_sqlite_snapshot_while_wal_is_active()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "before");
        var path = Path.Combine(temp.Path, "wal.moment-backup");
        await using (var writer = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={TestBackupFactory.DatabasePath(temp.Path)};Pooling=False"))
        {
            await writer.OpenAsync();
            await using (var wal = writer.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync();
            }
            await using (var update = writer.CreateCommand())
            {
                update.CommandText =
                    "UPDATE settings SET value = 'in-wal' WHERE key = 'test_marker';";
                await update.ExecuteNonQueryAsync();
            }

            await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        }

        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "changed");
        await TestBackupFactory.Create(temp.Path).RestoreAsync(path, default);

        Assert.Equal("in-wal", await TestBackupFactory.ReadMarkerAsync(temp.Path));
    }

    private sealed class RecordingLifecycle : IBackupRestoreLifecycle
    {
        private bool _refreshFailed;
        public List<string> Events { get; } = [];
        public bool FailFirstRefresh { get; init; }

        public Task StopAsync(CancellationToken ct)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            Events.Add("start");
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken ct)
        {
            Events.Add("refresh");
            if (FailFirstRefresh && !_refreshFailed)
            {
                _refreshFailed = true;
                throw new InvalidOperationException("refresh failed");
            }
            return Task.CompletedTask;
        }
    }
}
