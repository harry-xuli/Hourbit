namespace Hourbit.Infrastructure.Data;

public sealed record DataResetRequest(
    string DatabasePath,
    string BackupPath,
    DateTimeOffset RequestedAtUtc);

public interface IDataResetRequestStore
{
    Task WriteAsync(DataResetRequest request, CancellationToken ct);
    Task<DataResetRequest?> ReadAsync(CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
}

public interface IDataResetApplier
{
    Task<bool> ApplyPendingAsync(string expectedDatabasePath, CancellationToken ct);
}
