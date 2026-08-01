namespace Moment.App.Tests.Startup;

using System.IO;
using Moment.App.Startup;
using Moment.App.Timeline;
using Moment.Core.Scheduling;
using Moment.TestSupport;

public sealed class ApplicationBootstrapTests
{
    [Fact]
    public async Task Scheduler_state_change_eventually_refreshes_timeline_on_dispatcher()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var clock = new FakeClock("2026-08-01T20:04:00+08:00");
            var repository = new FakeReminderRepository();
            var sink = new RecordingReminderSink();
            await repository.AddAsync(TestData.Scheduled(
                "state change", clock.Now.AddMinutes(1).ToString("O")));
            using var scheduler = new ReminderScheduler(repository, sink, clock);
            var refreshed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = new TimelineRefreshCoordinator(dispatcher, () =>
            {
                Assert.True(dispatcher.CheckAccess());
                refreshed.TrySetResult();
                return Task.CompletedTask;
            });
            var failures = new List<Exception>();
            var handler = CompositionRoot.ComposeTimelineRefreshHandler(
                refresh, failures.Add, CancellationToken.None);
            scheduler.StateChanged += handler;
            try
            {
                await scheduler.StartAsync(CancellationToken.None);
                clock.AdvanceBy(TimeSpan.FromMinutes(1));

                await sink.WaitForCountAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
                await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Empty(failures);
            }
            finally
            {
                scheduler.StateChanged -= handler;
                await scheduler.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task Startup_recovery_finishes_its_first_reload_before_runtime_signals_start()
    {
        var reloadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startupContinued = false;
        var coordinator = new ReminderRecoveryCoordinator(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            async _ =>
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task;
            },
            new FakeClock("2026-08-01T20:04:00+08:00"),
            CancellationToken.None);
        await using (coordinator)
        {
            var startup = CompositionRoot.RecoverBeforeStartingRuntimeAsync(
                coordinator,
                () => startupContinued = true,
                CancellationToken.None);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(startupContinued);
            releaseReload.TrySetResult();
            await startup;
            Assert.True(startupContinued);
        }
    }

    [Fact]
    public void Windows_app_runtime_base_directory_is_set_for_single_file_startup()
    {
        const string variableName = "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY";
        var original = Environment.GetEnvironmentVariable(
            variableName, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                variableName, @"D:\StaleRuntimeLocation", EnvironmentVariableTarget.Process);

            ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();

            Assert.Equal(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(
                variableName, EnvironmentVariableTarget.Process));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                variableName, original, EnvironmentVariableTarget.Process);
        }
    }

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
    public async Task Missing_process_windows_directory_is_restored_before_WPF_font_URI_is_constructed()
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
