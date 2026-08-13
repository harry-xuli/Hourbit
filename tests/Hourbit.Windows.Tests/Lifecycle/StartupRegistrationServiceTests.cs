using Hourbit.Windows.Startup;

namespace Hourbit.Windows.Tests.Lifecycle;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void Default_is_disabled_and_enable_uses_exact_quoted_background_command()
    {
        var registry = new Registry();
        var service = new StartupRegistrationService(registry);
        var path = Path.GetFullPath(@"portable\Hourbit.exe");

        Assert.False(service.IsEnabled);
        Assert.Equal(StartupPathStatus.Disabled, service.GetPathStatus(path));
        service.SetEnabled(true, path);

        Assert.True(service.IsEnabled);
        Assert.Equal($"\"{path}\" --background", registry.Value);
        Assert.Equal(StartupPathStatus.Current, service.GetPathStatus(path));
        service.SetEnabled(false, path);
        Assert.Null(registry.Value);
    }

    [Fact]
    public void Existing_registration_for_moved_portable_executable_is_stale()
    {
        var registry = new Registry { Value = "\"C:\\Old\\Hourbit.exe\" --background" };
        var service = new StartupRegistrationService(registry);

        Assert.Equal(StartupPathStatus.Stale, service.GetPathStatus(@"C:\New\Hourbit.exe"));
    }

    private sealed class Registry : IStartupRegistry
    {
        public string? Value { get; set; }
        public string? Read() => Value;
        public void Write(string value) => Value = value;
        public void Delete() => Value = null;
    }
}
