namespace Moment.App.Tests.Startup;

using System.IO;
using Moment.App.Startup;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Scheduling;
using Moment.Core.Services;
using Moment.TestSupport;
using Moment.Windows.Alerts;
using Moment.Windows.Notifications;

public sealed class ApplicationBootstrapTests
{
    [Fact]
    public async Task Runtime_failure_reporting_forwards_recovery_and_important_alert_errors()
    {
        var now = DateTimeOffset.Parse("2026-08-01T20:04:00+08:00");
        var repository = new FakeReminderRepository();
        await repository.AddAsync(TestData.Scheduled(
            "delivery failure", now.AddMinutes(-1).ToString("O")));
        var recoveryFailure = new InvalidOperationException("recovery delivery failed");
        var deliverySink = new ThrowingDeliverySink(recoveryFailure);
        var recoveryService = new ReminderRecoveryService(
            repository, deliverySink, new RecoverySummarySink(deliverySink));
        var alertFailure = new InvalidOperationException("important action failed");
        await using var importantAlerts = new ImportantAlertController(
            new ImmediateImportantPresenter(),
            new ThrowingImportantActions(alertFailure));
        var reported = new List<Exception>();
        var allReported = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var reporting = CompositionRoot.ConnectRuntimeFailureReporting(
            recoveryService, importantAlerts, Report);

        _ = await recoveryService.RecoverAsync(now, CancellationToken.None);
        await importantAlerts.AdmitAsync(
            new ReminderAlert(Guid.NewGuid(), "important", now),
            CancellationToken.None);
        await allReported.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([recoveryFailure, alertFailure], reported);
        return;

        void Report(Exception exception)
        {
            reported.Add(exception);
            if (reported.Count == 2)
                allReported.TrySetResult();
        }
    }

    [Fact]
    public async Task Blocked_important_alert_does_not_stall_later_missed_recovery_restart_or_refresh()
    {
        var now = DateTimeOffset.Parse("2026-08-01T20:04:00+08:00");
        var repository = new FakeReminderRepository();
        var important = TestData.Scheduled(
            "important", now.AddHours(-2).ToString("O"), ReminderImportance.Important);
        var expired = TestData.Scheduled(
            "expired normal", now.AddHours(-1).ToString("O"));
        await repository.AddAsync(important);
        await repository.AddAsync(expired);
        var presenter = new BlockingImportantPresenter();
        var actions = new RecordingImportantActions();
        var importantAlerts = new ImportantAlertController(presenter, actions);
        var platform = new RecordingNotificationPlatform();
        var notificationSink = new AppNotificationSink(platform, importantAlerts, actions);
        var recoveryService = new ReminderRecoveryService(
            repository, notificationSink, new RecoverySummarySink(notificationSink));
        var schedulerStarts = 0;
        var timelineRefreshes = 0;
        var coordinator = new ReminderRecoveryCoordinator(
            _ => Task.CompletedTask,
            async (recoveryNow, ct) =>
                _ = await recoveryService.RecoverAsync(recoveryNow, ct),
            _ =>
            {
                Interlocked.Increment(ref schedulerStarts);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref timelineRefreshes);
                return Task.CompletedTask;
            },
            new FakeClock(now),
            CancellationToken.None);

        try
        {
            var recovery = coordinator.RecoverAndRefreshAsync(CancellationToken.None);
            await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await recovery.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(presenter.ActionFinished.Task.IsCompleted);
            Assert.Empty(actions.Calls);
            Assert.Equal(OccurrenceState.Fired,
                (await repository.GetScheduledReminderAsync(
                    important.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
            Assert.Equal(OccurrenceState.Missed,
                (await repository.GetScheduledReminderAsync(
                    expired.Occurrence.Id, CancellationToken.None))!.Occurrence.State);
            var summary = Assert.Single(platform.Payloads);
            Assert.Equal("missed-summary", summary.Tag);
            Assert.Equal(1, Volatile.Read(ref schedulerStarts));
            Assert.Equal(1, Volatile.Read(ref timelineRefreshes));

            presenter.Release();
            await presenter.ActionFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await actions.Called.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            presenter.Release();
            await coordinator.DisposeAsync();
            await importantAlerts.DisposeAsync();
        }
    }

    [Fact]
    public async Task Backup_restore_pre_cancelled_refresh_joins_active_reload_before_rollback_boundary()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            var timelineRefresh = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                Interlocked.Increment(ref reloads);
                reloadEntered.TrySetResult();
                await releaseReload.Task;
            });
            using var scheduler = new ReminderScheduler(
                new FakeReminderRepository(),
                new RecordingReminderSink(),
                new FakeClock("2026-08-01T20:04:00+08:00"));
            var lifecycle = new CompositionRoot.ConfigurableBackupRestoreLifecycle();
            lifecycle.Configure(scheduler, timelineRefresh);
            using var cancellation = new CancellationTokenSource();
            var active = timelineRefresh.RequestAsync(CancellationToken.None);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            var rollbackStarted = false;

            var restoreBoundary = ObserveRollbackBoundaryAsync();
            await Task.Yield();

            Assert.False(rollbackStarted);
            Assert.False(restoreBoundary.IsCompleted);
            releaseReload.TrySetResult();
            await restoreBoundary.WaitAsync(TimeSpan.FromSeconds(1));
            await active.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(rollbackStarted);
            Assert.Equal(1, Volatile.Read(ref reloads));
            await timelineRefresh.DisposeAsync();

            async Task ObserveRollbackBoundaryAsync()
            {
                try
                {
                    await lifecycle.RefreshAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    rollbackStarted = true;
                }
            }
        });
    }

    [Fact]
    public async Task Backup_restore_cancellation_waits_for_admitted_reload_before_rollback_boundary()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var timelineRefresh = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task;
            });
            using var scheduler = new ReminderScheduler(
                new FakeReminderRepository(),
                new RecordingReminderSink(),
                new FakeClock("2026-08-01T20:04:00+08:00"));
            var lifecycle = new CompositionRoot.ConfigurableBackupRestoreLifecycle();
            lifecycle.Configure(scheduler, timelineRefresh);
            using var cancellation = new CancellationTokenSource();
            var rollbackStarted = false;

            var restoreBoundary = ObserveRollbackBoundaryAsync();
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            await Task.Yield();

            Assert.False(rollbackStarted);
            Assert.False(restoreBoundary.IsCompleted);
            releaseReload.TrySetResult();
            await restoreBoundary.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(rollbackStarted);
            await timelineRefresh.DisposeAsync();

            async Task ObserveRollbackBoundaryAsync()
            {
                try
                {
                    await lifecycle.RefreshAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    rollbackStarted = true;
                }
            }
        });
    }

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

    private sealed class RecoverySummarySink(IReminderSink sink) :
        IReminderRecoverySummarySink
    {
        public Task SendMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            sink.DeliverMissedSummaryAsync(reminders, ct);
    }

    private sealed class RecordingNotificationPlatform : INotificationPlatform
    {
        public NotificationHealth Health => NotificationHealth.Available;
        public List<NotificationPayload> Payloads { get; } = [];
        public Task ShowAsync(NotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
        public Task OpenSettingsAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingDeliverySink(Exception failure) : IReminderSink
    {
        public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct) =>
            Task.FromException(failure);
        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ImmediateImportantPresenter : IImportantAlertPresenter
    {
        public Task<ImportantAlertAction> ShowAsync(
            ReminderAlert alert, CancellationToken ct) =>
            Task.FromResult(ImportantAlertAction.Ignore);
    }

    private sealed class BlockingImportantPresenter : IImportantAlertPresenter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ActionFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImportantAlertAction> ShowAsync(
            ReminderAlert alert, CancellationToken ct)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            ActionFinished.TrySetResult();
            return ImportantAlertAction.Ignore;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingImportantActions : IReminderActionService
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) =>
            RecordAsync("complete:" + occurrenceId);
        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) =>
            RecordAsync("ignore:" + occurrenceId);
        public Task<ReminderOccurrence> SnoozeAsync(
            Guid occurrenceId, TimeSpan delay, CancellationToken ct)
        {
            Calls.Add("snooze" + delay.TotalMinutes + ":" + occurrenceId);
            Called.TrySetResult();
            return Task.FromResult(ReminderOccurrence.Schedule(
                Guid.NewGuid(), DateTimeOffset.UtcNow));
        }

        private Task RecordAsync(string call)
        {
            Calls.Add(call);
            Called.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingImportantActions(Exception failure) :
        IReminderActionService
    {
        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) =>
            Task.FromException(failure);
        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) =>
            Task.FromException(failure);
        public Task<ReminderOccurrence> SnoozeAsync(
            Guid occurrenceId, TimeSpan delay, CancellationToken ct) =>
            Task.FromException<ReminderOccurrence>(failure);
    }

}
