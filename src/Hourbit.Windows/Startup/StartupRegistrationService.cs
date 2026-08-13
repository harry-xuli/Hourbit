using Microsoft.Win32;

namespace Hourbit.Windows.Startup;

public enum StartupPathStatus
{
    Disabled,
    Current,
    Stale
}

public interface IStartupRegistrationService
{
    bool IsEnabled { get; }
    StartupPathStatus GetPathStatus(string executablePath);
    void SetEnabled(bool enabled, string executablePath);
}

public interface IStartupRegistry
{
    string? Read();
    void Write(string value);
    void Delete();
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private readonly IStartupRegistry _registry;

    public StartupRegistrationService(IStartupRegistry? registry = null) =>
        _registry = registry ?? new CurrentUserStartupRegistry();

    public bool IsEnabled => !string.IsNullOrEmpty(_registry.Read());

    public StartupPathStatus GetPathStatus(string executablePath)
    {
        var registered = _registry.Read();
        if (string.IsNullOrEmpty(registered))
            return StartupPathStatus.Disabled;
        return string.Equals(registered, Command(executablePath), StringComparison.OrdinalIgnoreCase)
            ? StartupPathStatus.Current
            : StartupPathStatus.Stale;
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled)
            _registry.Write(Command(executablePath));
        else
            _registry.Delete();
    }

    private static string Command(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{Path.GetFullPath(executablePath)}\" --background";
    }
}

/// <summary>HKCU Run-key adapter; automated tests inject <see cref="IStartupRegistry"/>.</summary>
public sealed class CurrentUserStartupRegistry : IStartupRegistry
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Moment";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
