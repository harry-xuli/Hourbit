namespace Moment.Infrastructure.Backup;

public sealed record BackupManifest(
    int FormatVersion,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string Sha256);
