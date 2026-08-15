namespace Hourbit.Infrastructure.Data;

public sealed class DataResetApplier(
    IDataResetRequestStore store,
    TimeProvider? timeProvider = null) : IDataResetApplier
{
    private static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> ApplyPendingAsync(
        string expectedDatabasePath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedDatabasePath);
        var request = await store.ReadAsync(ct);
        if (request is null)
            return false;

        if (_timeProvider.GetUtcNow() - request.RequestedAtUtc > MaxRequestAge)
        {
            await store.DeleteAsync(ct);
            return false;
        }

        if (!string.Equals(
                request.DatabasePath,
                expectedDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("重置请求的数据路径无效。");
        }
        if (!File.Exists(request.BackupPath))
            throw new InvalidDataException("重置备份不存在，未删除任何数据。");

        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(expectedDatabasePath))
            ?? throw new ArgumentException(
                "Database path must have a directory.", nameof(expectedDatabasePath));
        var quarantine = Path.Combine(
            dataDirectory, "quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(quarantine);

        try
        {
            MoveIntoQuarantine(expectedDatabasePath, quarantine);
            MoveIntoQuarantine(expectedDatabasePath + "-wal", quarantine);
            MoveIntoQuarantine(expectedDatabasePath + "-shm", quarantine);

            await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                expectedDatabasePath, ct))
            {
                await DatabaseMigrator.MigrateAsync(connection, ct);
            }

            await store.DeleteAsync(ct);
            DeleteDirectoryIfExists(quarantine);
            return true;
        }
        catch
        {
            RestoreFromQuarantine(quarantine, dataDirectory);
            DeleteDirectoryIfExists(quarantine);
            throw;
        }
    }

    private static void MoveIntoQuarantine(string path, string quarantine)
    {
        if (!File.Exists(path))
            return;
        File.Move(path, Path.Combine(quarantine, Path.GetFileName(path)));
    }

    private static void RestoreFromQuarantine(
        string quarantine,
        string dataDirectory)
    {
        if (!Directory.Exists(quarantine))
            return;
        foreach (var file in Directory.GetFiles(quarantine))
        {
            var destination = Path.Combine(dataDirectory, Path.GetFileName(file));
            File.Move(file, destination, overwrite: true);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
