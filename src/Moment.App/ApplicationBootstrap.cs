namespace Moment.App;

public static class ApplicationBootstrap
{
    public static void EnsureWindowsDirectoryEnvironment()
    {
        var processValue = Environment.GetEnvironmentVariable(
            "windir", EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(processValue))
            return;

        var machineValue = Environment.GetEnvironmentVariable(
            "windir", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(machineValue))
        {
            throw new InvalidOperationException(
                "Windows directory is unavailable from both the process and Machine environment.");
        }

        Environment.SetEnvironmentVariable(
            "windir", machineValue, EnvironmentVariableTarget.Process);
    }
}
