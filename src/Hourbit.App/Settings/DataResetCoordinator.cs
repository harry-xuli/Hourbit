using Hourbit.Core.Abstractions;
using Hourbit.Infrastructure.Backup;
using Hourbit.Infrastructure.Data;

namespace Hourbit.App.Settings;

public sealed record RequestResetResult(bool RestartRequired);

public interface IDataResetCoordinator
{
    Task<RequestResetResult> RequestAsync(
        string backupPath,
        CancellationToken ct);
}

public sealed class DataResetCoordinator(
    IBackupService backup,
    IDataResetRequestStore store,
    string databasePath,
    IClock clock,
    Action requestRestart) : IDataResetCoordinator
{
    public async Task<RequestResetResult> RequestAsync(
        string backupPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backupPath);
        await backup.ExportAsync(backupPath, ct);
        await store.WriteAsync(
            new DataResetRequest(databasePath, backupPath, clock.Now.ToUniversalTime()),
            ct);
        requestRestart();
        return new RequestResetResult(true);
    }
}
