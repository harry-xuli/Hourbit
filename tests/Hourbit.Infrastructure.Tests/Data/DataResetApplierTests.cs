using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Data;

public sealed class DataResetApplierTests
{
    [Fact]
    public async Task No_request_means_no_work()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        var applier = new DataResetApplier(new DataResetRequestStore(dbPath));

        Assert.False(await applier.ApplyPendingAsync(dbPath, default));
    }

    [Fact]
    public async Task Stale_request_is_deleted_and_ignored()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        var backupPath = Path.Combine(temp.Path, "backup.moment-backup");
        await File.WriteAllTextAsync(backupPath, "backup", default);
        var store = new DataResetRequestStore(dbPath);
        await store.WriteAsync(new DataResetRequest(
            dbPath, backupPath, DateTimeOffset.UtcNow.AddMinutes(-31)), default);
        var applier = new DataResetApplier(
            store, new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.False(await applier.ApplyPendingAsync(dbPath, default));
        Assert.Null(await store.ReadAsync(default));
    }

    [Fact]
    public async Task Wrong_database_path_is_rejected_before_deleting_anything()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        await CreateMigratedDatabaseAsync(dbPath);
        var backupPath = Path.Combine(temp.Path, "backup.moment-backup");
        await File.WriteAllTextAsync(backupPath, "backup", default);
        var store = new DataResetRequestStore(dbPath);
        await store.WriteAsync(new DataResetRequest(
            dbPath, backupPath, DateTimeOffset.UtcNow), default);
        var applier = new DataResetApplier(store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            applier.ApplyPendingAsync(Path.Combine(temp.Path, "other.db"), default));
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task Missing_backup_is_rejected_and_original_data_is_preserved()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        await CreateMigratedDatabaseAsync(dbPath);
        var store = new DataResetRequestStore(dbPath);
        await store.WriteAsync(new DataResetRequest(
            dbPath, Path.Combine(temp.Path, "missing.moment-backup"),
            DateTimeOffset.UtcNow), default);
        var applier = new DataResetApplier(store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            applier.ApplyPendingAsync(dbPath, default));
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task Valid_request_quarantines_and_recreates_an_empty_database()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        await CreateMigratedDatabaseAsync(dbPath);
        var sentinel = Path.Combine(temp.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep", default);
        var backupPath = Path.Combine(temp.Path, "backup.moment-backup");
        await File.WriteAllTextAsync(backupPath, "backup", default);
        var store = new DataResetRequestStore(dbPath);
        await store.WriteAsync(new DataResetRequest(
            dbPath, backupPath, DateTimeOffset.UtcNow), default);
        var applier = new DataResetApplier(store);

        Assert.True(await applier.ApplyPendingAsync(dbPath, default));

        Assert.True(File.Exists(dbPath));
        Assert.Null(await store.ReadAsync(default));
        Assert.True(File.Exists(backupPath));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task Malformed_request_is_rejected()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        var store = new DataResetRequestStore(dbPath);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "data-reset-request.json"),
            "{ not json", default);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ReadAsync(default));
    }

    private static async Task CreateMigratedDatabaseAsync(string path)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(path, default);
        await DatabaseMigrator.MigrateAsync(connection, default);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
