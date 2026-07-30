namespace Moment.App.Tests.Startup;

using System.IO;

public sealed class ApplicationBootstrapTests
{
    [Fact]
    public void Existing_process_windows_directory_is_not_overwritten()
    {
        var original = Environment.GetEnvironmentVariable("windir", EnvironmentVariableTarget.Process);
        try
        {
            const string existing = @"D:\ExistingWindows";
            Environment.SetEnvironmentVariable("windir", existing, EnvironmentVariableTarget.Process);

            ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();

            Assert.Equal(existing,
                Environment.GetEnvironmentVariable("windir", EnvironmentVariableTarget.Process));
        }
        finally
        {
            Environment.SetEnvironmentVariable("windir", original, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task Missing_process_windows_directory_is_restored_before_application_resources_load()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var original = Environment.GetEnvironmentVariable(
                "windir", EnvironmentVariableTarget.Process);
            try
            {
                Environment.SetEnvironmentVariable(
                    "windir", null, EnvironmentVariableTarget.Process);

                ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();

                var machine = Environment.GetEnvironmentVariable(
                    "windir", EnvironmentVariableTarget.Machine);
                Assert.False(string.IsNullOrWhiteSpace(machine));
                Assert.Equal(machine, Environment.GetEnvironmentVariable(
                    "windir", EnvironmentVariableTarget.Process));
                Assert.True(new Uri(Path.Combine(machine!, "Fonts") + Path.DirectorySeparatorChar,
                    UriKind.Absolute).IsAbsoluteUri);

                var application = new App();
                application.InitializeComponent();
                Assert.NotNull(application.Resources["PrimaryTextBrush"]);
                Assert.NotNull(application.Resources["KeyboardFocusVisual"]);
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetResult(exception);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "windir", original, EnvironmentVariableTarget.Process);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(exception);
    }
}
