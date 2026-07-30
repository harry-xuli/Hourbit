namespace Moment.Core.Abstractions;

public sealed record AppSettings(
    string Hotkey,
    bool StartWithWindows,
    int AlertVolume,
    string? CustomAlertSoundPath);

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
