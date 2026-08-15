using System.IO;
using Hourbit.App.Settings;
using Hourbit.Infrastructure.Backup;
using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Settings;

public sealed class DataResetCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Request_exports_backup_writes_request_and_requests_restart()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        var backupPath = Path.Combine(temp.Path, "backup.moment-backup");
        var backup = new RecordingBackupService();
        var store = new DataResetRequestStore(dbPath);
        var restarts = 0;
        var coordinator = new DataResetCoordinator(
            backup, store, dbPath, new FakeClock(Now), () => restarts++);

        var result = await coordinator.RequestAsync(backupPath, default);

        Assert.True(result.RestartRequired);
        Assert.Equal(backupPath, Assert.Single(backup.ExportedPaths));
        Assert.Equal(1, restarts);
        var request = await store.ReadAsync(default);
        Assert.NotNull(request);
        Assert.Equal(dbPath, request!.DatabasePath);
        Assert.Equal(backupPath, request.BackupPath);
    }

    [Fact]
    public async Task Export_failure_writes_no_request_and_does_not_restart()
    {
        using var temp = new TempDirectory();
        var dbPath = Path.Combine(temp.Path, "moment.db");
        var store = new DataResetRequestStore(dbPath);
        var restarts = 0;
        var coordinator = new DataResetCoordinator(
            new ThrowingBackupService(), store, dbPath,
            new FakeClock(Now), () => restarts++);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RequestAsync(
                Path.Combine(temp.Path, "x.moment-backup"), default));

        Assert.Null(await store.ReadAsync(default));
        Assert.Equal(0, restarts);
    }

    private sealed class RecordingBackupService : IBackupService
    {
        public List<string> ExportedPaths { get; } = [];

        public Task<string> CreateDailyBackupAsync(CancellationToken ct) =>
            Task.FromResult(string.Empty);

        public Task ExportAsync(string destinationPath, CancellationToken ct)
        {
            ExportedPaths.Add(destinationPath);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(string backupPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingBackupService : IBackupService
    {
        public Task<string> CreateDailyBackupAsync(CancellationToken ct) =>
            Task.FromResult(string.Empty);

        public Task ExportAsync(string destinationPath, CancellationToken ct) =>
            throw new InvalidOperationException("export failed");

        public Task RestoreAsync(string backupPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
