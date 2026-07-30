using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moment.Infrastructure.Data;

namespace Moment.Infrastructure.Backup;

public interface IBackupService
{
    Task<string> CreateDailyBackupAsync(CancellationToken ct);
    Task ExportAsync(string destinationPath, CancellationToken ct);
    Task RestoreAsync(string backupPath, CancellationToken ct);
}

public interface IBackupRestoreLifecycle
{
    Task StopAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task RefreshAsync(CancellationToken ct);
}

public sealed class BackupService : IBackupService
{
    private const string LastBackupDateKey = "last_successful_local_backup_date";
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly IBackupRestoreLifecycle _lifecycle;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _localZone;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public BackupService(
        string databasePath,
        string backupDirectory,
        IBackupRestoreLifecycle? lifecycle = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeZoneInfo? localZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _lifecycle = lifecycle ?? NoopBackupRestoreLifecycle.Instance;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    public async Task<string> CreateDailyBackupAsync(CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var now = _utcNow().ToUniversalTime();
            var localDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(now, _localZone).DateTime);
            var localDateText = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(
                    await ReadSettingAsync(LastBackupDateKey, ct),
                    localDateText,
                    StringComparison.Ordinal))
            {
                return GetNewestAutomaticBackup() ?? string.Empty;
            }

            Directory.CreateDirectory(_backupDirectory);
            var path = Path.Combine(
                _backupDirectory,
                $"moment-{now:yyyyMMdd'T'HHmmssfff'Z'}.moment-backup");
            await ExportCoreAsync(path, now, ct);
            RetainNewestAutomaticBackups();
            await WriteSettingAsync(LastBackupDateKey, localDateText, ct);
            return path;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ExportAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await _operationGate.WaitAsync(ct);
        try
        {
            await ExportCoreAsync(destinationPath, _utcNow().ToUniversalTime(), ct);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestoreAsync(string backupPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        await _operationGate.WaitAsync(ct);
        try
        {
            await RestoreCoreAsync(backupPath, ct);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ExportCoreAsync(
        string destinationPath,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(destination, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup destination cannot replace the live database.");

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new InvalidOperationException("Backup destination must have a parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        var token = Guid.NewGuid().ToString("N");
        var snapshot = Path.Combine(destinationDirectory, $".moment-{token}.snapshot.db");
        var package = Path.Combine(destinationDirectory, $".moment-{token}.moment-backup.tmp");
        try
        {
            await BackupPackage.CreateSqliteSnapshotAsync(_databasePath, snapshot, ct);
            var schemaVersion = await BackupPackage.ReadSchemaVersionAsync(snapshot, ct);
            BackupPackage.EnsureCompatibleSchema(schemaVersion);
            var hash = await BackupPackage.ComputeSha256Async(snapshot, ct);
            var manifest = new BackupManifest(
                BackupPackage.FormatVersion,
                schemaVersion,
                createdAt,
                hash);
            await BackupPackage.WriteAsync(package, snapshot, manifest, ct);
            BackupPackage.AtomicReplace(package, destination);
        }
        finally
        {
            BackupPackage.TryDelete(snapshot);
            BackupPackage.TryDelete(package);
        }
    }

    private async Task RestoreCoreAsync(string backupPath, CancellationToken ct)
    {
        var databaseDirectory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            throw new InvalidOperationException("Database path must have a parent directory.");
        Directory.CreateDirectory(databaseDirectory);

        var verified = await BackupPackage.VerifyAndExtractAsync(
            Path.GetFullPath(backupPath), databaseDirectory, ct);
        var safetyPath = Path.Combine(
            databaseDirectory, $".moment-{Guid.NewGuid():N}.restore-safety.db");
        var stopped = false;
        try
        {
            await _lifecycle.StopAsync(ct);
            stopped = true;
            await BackupPackage.CreateSqliteSnapshotAsync(
                _databasePath, safetyPath, ct);
            BackupPackage.AtomicReplace(verified.DatabasePath, _databasePath);
            await MigrateAndVerifyAsync(_databasePath, ct);
            await _lifecycle.StartAsync(ct);
            await _lifecycle.RefreshAsync(ct);
        }
        catch (Exception original) when (stopped)
        {
            Exception? rollbackFailure = null;
            try
            {
                if (File.Exists(safetyPath))
                    BackupPackage.AtomicReplace(safetyPath, _databasePath);
                await _lifecycle.StartAsync(CancellationToken.None);
                await _lifecycle.RefreshAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                rollbackFailure = exception;
            }

            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Restore failed and rollback could not fully restart the application.",
                    original,
                    rollbackFailure);
            }
            throw;
        }
        finally
        {
            BackupPackage.TryDelete(verified.DatabasePath);
            BackupPackage.TryDelete(safetyPath);
        }
    }

    private static async Task MigrateAndVerifyAsync(
        string databasePath,
        CancellationToken ct)
    {
        await using (var connection =
                     await DatabaseMigrator.OpenConnectionAsync(databasePath, ct))
        {
            await DatabaseMigrator.MigrateAsync(connection, ct);
        }
        if (!await BackupPackage.CheckIntegrityAsync(
                databasePath, fullCheck: true, ct))
        {
            throw new InvalidDataException(
                "The restored database failed SQLite integrity validation.");
        }
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private async Task WriteSettingAsync(
        string key,
        string value,
        CancellationToken ct)
    {
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(_databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private string? GetNewestAutomaticBackup()
    {
        if (!Directory.Exists(_backupDirectory))
            return null;
        return Directory.EnumerateFiles(
                _backupDirectory,
                "moment-*.moment-backup",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private void RetainNewestAutomaticBackups()
    {
        foreach (var path in Directory.EnumerateFiles(
                     _backupDirectory,
                     "moment-*.moment-backup",
                     SearchOption.TopDirectoryOnly)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(7))
        {
            File.Delete(path);
        }
    }

    private sealed class NoopBackupRestoreLifecycle : IBackupRestoreLifecycle
    {
        internal static NoopBackupRestoreLifecycle Instance { get; } = new();
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

internal sealed record VerifiedBackup(
    string DatabasePath,
    BackupManifest Manifest);

internal static class BackupPackage
{
    internal const int FormatVersion = 1;
    internal const int CurrentSchemaVersion = 2;
    internal const long MaximumDatabaseBytes = 1024L * 1024L * 1024L;
    private const long MaximumManifestBytes = 64L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static async Task WriteAsync(
        string packagePath,
        string databasePath,
        BackupManifest manifest,
        CancellationToken ct)
    {
        await using var output = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var databaseEntry = archive.CreateEntry(
            "moment.db", CompressionLevel.Optimal);
        await using (var entryOutput = databaseEntry.Open())
        await using (var databaseInput = new FileStream(
                         databasePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await databaseInput.CopyToAsync(entryOutput, ct);
        }

        var manifestEntry = archive.CreateEntry(
            "manifest.json", CompressionLevel.Optimal);
        await using var manifestOutput = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(
            manifestOutput, manifest, JsonOptions, ct);
    }

    internal static async Task<VerifiedBackup> VerifyAndExtractAsync(
        string packagePath,
        string stagingDirectory,
        CancellationToken ct)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Backup package was not found.", packagePath);
        if (new FileInfo(packagePath).Length > MaximumDatabaseBytes)
            throw new InvalidDataException("Backup package exceeds the 1 GiB safety limit.");

        Directory.CreateDirectory(stagingDirectory);
        var extracted = Path.Combine(
            stagingDirectory, $".moment-{Guid.NewGuid():N}.verified.db");
        try
        {
            BackupManifest manifest;
            try
            {
                using var archive = ZipFile.OpenRead(packagePath);
                ValidateEntries(archive);
                var databaseEntry = archive.Entries.Single(
                    entry => entry.FullName == "moment.db");
                var manifestEntry = archive.Entries.Single(
                    entry => entry.FullName == "manifest.json");
                if (databaseEntry.Length > MaximumDatabaseBytes)
                    throw new InvalidDataException(
                        "Backup database exceeds the 1 GiB safety limit.");
                if (manifestEntry.Length > MaximumManifestBytes)
                    throw new InvalidDataException("Backup manifest is too large.");

                await using (var manifestStream = manifestEntry.Open())
                {
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                                   manifestStream, JsonOptions, ct)
                               ?? throw new InvalidDataException(
                                   "Backup manifest is missing.");
                }
                ValidateManifest(manifest);

                await using var entryInput = databaseEntry.Open();
                await using var databaseOutput = new FileStream(
                    extracted,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyWithLimitAsync(
                    entryInput, databaseOutput, MaximumDatabaseBytes, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or NotSupportedException)
            {
                throw new InvalidDataException(
                    "Backup package is corrupt or unreadable.", exception);
            }

            var hash = await ComputeSha256Async(extracted, ct);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash),
                    Convert.FromHexString(manifest.Sha256)))
            {
                throw new InvalidDataException(
                    "Backup database checksum does not match the manifest.");
            }

            var actualSchema = await ReadSchemaVersionAsync(extracted, ct);
            if (actualSchema != manifest.SchemaVersion)
            {
                throw new InvalidDataException(
                    "Backup manifest schema does not match the database.");
            }
            if (!await CheckIntegrityAsync(extracted, fullCheck: true, ct))
            {
                throw new InvalidDataException(
                    "Backup database failed SQLite integrity validation.");
            }
            return new VerifiedBackup(extracted, manifest);
        }
        catch
        {
            TryDelete(extracted);
            throw;
        }
    }

    internal static async Task CreateSqliteSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Live database was not found.", sourcePath);
        TryDelete(destinationPath);
        await using var connection =
            await DatabaseMigrator.OpenConnectionAsync(sourcePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $destination;";
        command.Parameters.AddWithValue("$destination", destinationPath);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<int> ReadSchemaVersionAsync(
        string databasePath,
        CancellationToken ct)
    {
        try
        {
            await using var connection =
                await OpenReadOnlyAsync(databasePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_info;";
            var value = await command.ExecuteScalarAsync(ct);
            if (value is null or DBNull)
                throw new InvalidDataException("Backup database has no schema version.");
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException)
        {
            throw new InvalidDataException(
                "Backup database schema could not be read.", exception);
        }
    }

    internal static void EnsureCompatibleSchema(int schemaVersion)
    {
        if (schemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Backup schema version {schemaVersion} is not supported.");
        }
    }

    internal static async Task<bool> CheckIntegrityAsync(
        string databasePath,
        bool fullCheck,
        CancellationToken ct)
    {
        try
        {
            await using var connection =
                await OpenReadOnlyAsync(databasePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = fullCheck
                ? "PRAGMA integrity_check;"
                : "PRAGMA quick_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var sawRow = false;
            while (await reader.ReadAsync(ct))
            {
                sawRow = true;
                if (!string.Equals(
                        reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return sawRow;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, ct);
        return Convert.ToHexStringLower(hash);
    }

    internal static void AtomicReplace(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(sourcePath, destinationPath, null, ignoreMetadataErrors: true);
        else
            File.Move(sourcePath, destinationPath);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a uniquely named staging artifact.
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken ct)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count != 2)
            throw new InvalidDataException(
                "Backup package must contain exactly moment.db and manifest.json.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
                throw new InvalidDataException("Backup package contains duplicate entries.");
            if (entry.FullName is not ("moment.db" or "manifest.json"))
            {
                throw new InvalidDataException(
                    "Backup package contains an unexpected or unsafe entry.");
            }
        }
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                $"Backup format version {manifest.FormatVersion} is not supported.");
        }
        EnsureCompatibleSchema(manifest.SchemaVersion);
        if (manifest.CreatedAt == default)
            throw new InvalidDataException("Backup manifest has no creation time.");
        if (manifest.Sha256 is null ||
            manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "Backup manifest has an invalid SHA-256 checksum.");
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
                return;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException(
                    "Backup database exceeds the 1 GiB safety limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
