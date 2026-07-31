using Moment.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace Moment.Infrastructure.Backup;

public enum DatabaseRecoveryStatus
{
    Ready,
    Restored,
    RequiresUserDecision
}

public sealed record DatabaseRecoveryResult(
    DatabaseRecoveryStatus Status,
    string? CorruptDatabasePath = null,
    string? RestoredBackupPath = null);

public sealed class DatabaseRecoveryService
{
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly Func<DateTimeOffset> _utcNow;

    public DatabaseRecoveryService(
        string databasePath,
        string backupDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DatabaseRecoveryResult> OpenWithRecoveryAsync(
        CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
            return new DatabaseRecoveryResult(DatabaseRecoveryStatus.Ready);
        if (await BackupPackage.CheckIntegrityAsync(
                _databasePath, fullCheck: false, ct))
        {
            return new DatabaseRecoveryResult(DatabaseRecoveryStatus.Ready);
        }

        var corruptCopy = CreateCorruptCopyPath();
        File.Copy(_databasePath, corruptCopy, overwrite: false);
        if (Directory.Exists(_backupDirectory))
        {
            foreach (var backupPath in
                     BackupPackage.GetAutomaticBackupPaths(_backupDirectory))
            {
                ct.ThrowIfCancellationRequested();
                VerifiedBackup? verified = null;
                try
                {
                    verified = await PrepareBackupAsync(backupPath, ct);

                    BackupPackage.AtomicReplace(
                        verified.DatabasePath, _databasePath);
                    return new DatabaseRecoveryResult(
                        DatabaseRecoveryStatus.Restored,
                        corruptCopy,
                        backupPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException
                        or SqliteException)
                {
                    // Invalid automatic backups are skipped newest-first.
                }
                finally
                {
                    if (verified is not null)
                        BackupPackage.TryDelete(verified.DatabasePath);
                }
            }
        }

        return new DatabaseRecoveryResult(
            DatabaseRecoveryStatus.RequiresUserDecision,
            corruptCopy);
    }

    public async Task RestoreUserSelectedAsync(
        string backupPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var verified = await PrepareBackupAsync(
            Path.GetFullPath(backupPath), ct);
        try
        {
            BackupPackage.AtomicReplace(
                verified.DatabasePath, _databasePath);
        }
        finally
        {
            BackupPackage.TryDelete(verified.DatabasePath);
        }
    }

    private async Task<VerifiedBackup> PrepareBackupAsync(
        string backupPath,
        CancellationToken ct)
    {
        var stagingDirectory =
            Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException(
                "Database path must have a parent directory.");
        var verified = await BackupPackage.VerifyAndExtractAsync(
            backupPath, stagingDirectory, ct);
        try
        {
            await using (var connection =
                         await DatabaseMigrator.OpenConnectionAsync(
                             verified.DatabasePath, ct))
            {
                await DatabaseMigrator.MigrateAsync(connection, ct);
            }
            if (!await BackupPackage.CheckIntegrityAsync(
                    verified.DatabasePath, fullCheck: true, ct))
            {
                throw new InvalidDataException(
                    "Selected backup failed SQLite integrity validation after migration.");
            }
            return verified;
        }
        catch
        {
            BackupPackage.TryDelete(verified.DatabasePath);
            throw;
        }
    }

    private string CreateCorruptCopyPath()
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException(
                "Database path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var timestamp = _utcNow().ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"moment.db.corrupt-{timestamp}");
        if (!File.Exists(candidate))
            return candidate;
        return Path.Combine(
            directory,
            $"moment.db.corrupt-{timestamp}-{Guid.NewGuid():N}");
    }
}
