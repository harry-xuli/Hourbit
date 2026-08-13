using Hourbit.Infrastructure.Backup;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Backup;

public sealed class DatabaseRecoveryServiceTests
{
    [Fact]
    public async Task Corrupt_primary_uses_newest_valid_backup_and_preserves_bytes()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "older-valid");
        var now = DateTimeOffset.Parse("2026-07-28T01:02:03Z");
        var backup = TestBackupFactory.Create(temp.Path, () => now);
        var validPath = await backup.CreateDailyBackupAsync(default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "newer-corrupt");
        now = now.AddDays(1);
        var corruptBackup = await backup.CreateDailyBackupAsync(default);
        await TestBackupFactory.ReplaceWithUnmigratableDatabaseAsync(corruptBackup);
        var corruptBytes = new byte[] { 83, 81, 76, 105, 116, 101, 0, 1, 2, 3 };
        await File.WriteAllBytesAsync(
            TestBackupFactory.DatabasePath(temp.Path), corruptBytes);
        var recovery = new DatabaseRecoveryService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            () => DateTimeOffset.Parse("2026-07-30T01:02:03Z"));

        var result = await recovery.OpenWithRecoveryAsync(default);

        Assert.Equal(DatabaseRecoveryStatus.Restored, result.Status);
        Assert.Equal(validPath, result.RestoredBackupPath);
        Assert.Equal("older-valid",
            await TestBackupFactory.ReadMarkerAsync(temp.Path));
        Assert.NotNull(result.CorruptDatabasePath);
        Assert.Equal(corruptBytes,
            await File.ReadAllBytesAsync(result.CorruptDatabasePath!));
    }

    [Fact]
    public async Task Corrupt_primary_without_valid_backup_requires_user_decision_without_replacement()
    {
        using var temp = new TempDirectory();
        var corruptBytes = new byte[] { 83, 81, 76, 105, 116, 101, 4, 5, 6 };
        var databasePath = TestBackupFactory.DatabasePath(temp.Path);
        await File.WriteAllBytesAsync(databasePath, corruptBytes);
        var recovery = new DatabaseRecoveryService(
            databasePath,
            TestBackupFactory.BackupDirectory(temp.Path),
            () => DateTimeOffset.Parse("2026-07-30T01:02:03Z"));

        var result = await recovery.OpenWithRecoveryAsync(default);

        Assert.Equal(DatabaseRecoveryStatus.RequiresUserDecision, result.Status);
        Assert.Null(result.RestoredBackupPath);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(databasePath));
        Assert.NotNull(result.CorruptDatabasePath);
        Assert.Equal(corruptBytes,
            await File.ReadAllBytesAsync(result.CorruptDatabasePath!));
    }

    [Fact]
    public async Task Recovery_ignores_manual_and_malformed_moment_packages()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "automatic");
        var now = DateTimeOffset.Parse("2026-07-28T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);
        var automatic = await service.CreateDailyBackupAsync(default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "not automatic");
        var manual = Path.Combine(
            TestBackupFactory.BackupDirectory(temp.Path),
            "moment-export-user.moment-backup");
        var malformed = Path.Combine(
            TestBackupFactory.BackupDirectory(temp.Path),
            "moment-99999999T999999999Z.moment-backup");
        await service.ExportAsync(manual, default);
        File.Copy(manual, malformed);
        await File.WriteAllBytesAsync(
            TestBackupFactory.DatabasePath(temp.Path),
            [83, 81, 76, 105, 116, 101, 9, 8, 7]);
        var recovery = new DatabaseRecoveryService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            () => now.AddDays(1));

        var result = await recovery.OpenWithRecoveryAsync(default);

        Assert.Equal(DatabaseRecoveryStatus.Restored, result.Status);
        Assert.Equal(automatic, result.RestoredBackupPath);
        Assert.Equal("automatic",
            await TestBackupFactory.ReadMarkerAsync(temp.Path));
    }
}
