using Microsoft.Data.Sqlite;
using Hourbit.App.Startup;
using Hourbit.Infrastructure.Backup;
using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;
using System.IO;

namespace Hourbit.App.Tests.Startup;

public sealed class DatabaseRecoveryWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_decline_confirmation_leaves_corrupt_primary_unchanged(
        bool selectBackup)
    {
        using var temp = new TempDirectory();
        var databasePath = Path.Combine(temp.Path, "moment.db");
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var backupPath = await CreateBackupAsync(
            databasePath, backupDirectory, "backup");
        var corruptBytes = new byte[] { 83, 81, 76, 105, 116, 101, 4, 3, 2, 1 };
        await File.WriteAllBytesAsync(databasePath, corruptBytes);
        var recovery = new DatabaseRecoveryService(
            databasePath, backupDirectory, FixedUtcNow);
        var result = await recovery.OpenWithRecoveryAsync(default);
        var dialog = new RecordingRecoveryDialog(
            selectBackup ? backupPath : null,
            confirm: false);

        var recovered = await DatabaseRecoveryWorkflow.RunAsync(
            recovery, result, dialog, default);

        Assert.False(recovered);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(databasePath));
        Assert.Equal(selectBackup ? 1 : 0, dialog.Confirmations);
        Assert.NotNull(result.CorruptDatabasePath);
        Assert.Contains(
            result.CorruptDatabasePath!,
            dialog.CorruptCopyPaths);
    }

    [Fact]
    public async Task Explicitly_confirmed_selected_backup_recovers_before_composition()
    {
        using var temp = new TempDirectory();
        var databasePath = Path.Combine(temp.Path, "moment.db");
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var backupPath = await CreateBackupAsync(
            databasePath, backupDirectory, "restored");
        await File.WriteAllBytesAsync(
            databasePath,
            [83, 81, 76, 105, 116, 101, 8, 7, 6]);
        var recovery = new DatabaseRecoveryService(
            databasePath, backupDirectory, FixedUtcNow);
        var result = await recovery.OpenWithRecoveryAsync(default);
        var dialog = new RecordingRecoveryDialog(
            backupPath,
            confirm: true);

        var recovered = await DatabaseRecoveryWorkflow.RunAsync(
            recovery, result, dialog, default);

        Assert.True(recovered);
        Assert.Equal(1, dialog.Confirmations);
        Assert.Equal("restored", await ReadMarkerAsync(databasePath));
        Assert.Equal(
            result.CorruptDatabasePath,
            Assert.Single(dialog.CorruptCopyPaths));
    }

    private static DateTimeOffset FixedUtcNow() =>
        DateTimeOffset.Parse("2026-07-30T01:02:03Z");

    private static async Task<string> CreateBackupAsync(
        string databasePath,
        string backupDirectory,
        string marker)
    {
        await using (var connection =
                     await DatabaseMigrator.OpenConnectionAsync(
                         databasePath, default))
        {
            await DatabaseMigrator.MigrateAsync(connection, default);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value) VALUES ('test_marker', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", marker);
            await command.ExecuteNonQueryAsync();
        }
        var path = Path.Combine(backupDirectory, "selected.moment-backup");
        await new BackupService(
                databasePath,
                backupDirectory,
                utcNow: FixedUtcNow)
            .ExportAsync(path, default);
        return path;
    }

    private static async Task<string?> ReadMarkerAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM settings WHERE key = 'test_marker';";
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class RecordingRecoveryDialog(
        string? selectedPath,
        bool confirm) : IPreCompositionRecoveryDialog
    {
        public int Confirmations { get; private set; }
        public List<string> CorruptCopyPaths { get; } = [];

        public string? SelectBackup(string corruptCopyPath)
        {
            CorruptCopyPaths.Add(corruptCopyPath);
            return selectedPath;
        }

        public bool ConfirmRestore(
            string backupPath,
            string corruptCopyPath)
        {
            Confirmations++;
            Assert.Equal(selectedPath, backupPath);
            Assert.Equal(
                Assert.Single(CorruptCopyPaths),
                corruptCopyPath);
            return confirm;
        }
    }
}
