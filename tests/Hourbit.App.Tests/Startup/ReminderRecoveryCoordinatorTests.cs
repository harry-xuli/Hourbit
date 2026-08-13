using Hourbit.App.Startup;
using Hourbit.App.Timeline;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Startup;

public sealed class ReminderRecoveryCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-01T20:04:00+08:00");

    [Fact]
    public async Task Root_lifetime_cancellation_keeps_refresh_admitted_until_recovery_disposal_drains()
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
            using var lifetime = new CancellationTokenSource();
            var coordinator = CreateCoordinator(
                refresh: timelineRefresh.RequestAndDrainAsync,
                appLifetime: lifetime.Token);

            var recovery = coordinator.RecoverAndRefreshAsync(CancellationToken.None);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            lifetime.Cancel();
            var recoveryDisposal = coordinator.DisposeAsync().AsTask();
            var refreshDisposal = timelineRefresh.DisposeAsync().AsTask();

            Assert.False(recovery.IsCompleted);
            Assert.False(recoveryDisposal.IsCompleted);
            Assert.False(refreshDisposal.IsCompleted);
            releaseReload.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
            await recoveryDisposal.WaitAsync(TimeSpan.FromSeconds(1));
            await refreshDisposal.WaitAsync(TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public async Task Recovery_stops_scheduler_persists_restarts_then_refreshes()
    {
        var events = new List<string>();
        var coordinator = CreateCoordinator(
            stop: _ => RecordAsync("stop"),
            recover: (now, _) =>
            {
                Assert.Equal(Now, now);
                return RecordAsync("persist");
            },
            start: _ => RecordAsync("start"),
            refresh: _ => RecordAsync("reload"));
        await using (coordinator)
        {
            await coordinator.RecoverAndRefreshAsync(CancellationToken.None);
        }

        Assert.Equal(["stop", "persist", "start", "reload"], events);
        return;

        Task RecordAsync(string value)
        {
            events.Add(value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Recovery_failure_still_restarts_scheduler_and_skips_refresh()
    {
        var events = new List<string>();
        var expected = new InvalidOperationException("recovery failed");
        var coordinator = CreateCoordinator(
            stop: _ => RecordAsync("stop"),
            recover: (_, _) =>
            {
                events.Add("recover");
                return Task.FromException(expected);
            },
            start: _ => RecordAsync("start"),
            refresh: _ => RecordAsync("reload"));
        await using (coordinator)
        {
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.RecoverAndRefreshAsync(CancellationToken.None));
            Assert.Same(expected, observed);
        }

        Assert.Equal(["stop", "recover", "start"], events);
        return;

        Task RecordAsync(string value)
        {
            events.Add(value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Caller_cancellation_during_recovery_still_restarts_scheduler()
    {
        var recoveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerRestarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            recover: async (_, ct) =>
            {
                recoveryEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            start: ct =>
            {
                Assert.False(ct.IsCancellationRequested);
                schedulerRestarted.TrySetResult();
                return Task.CompletedTask;
            });
        await using (coordinator)
        {
            var recovery = coordinator.RecoverAndRefreshAsync(cancellation.Token);
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
            await schedulerRestarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task App_lifetime_cancellation_prevents_scheduler_restart()
    {
        var recoveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        var starts = 0;
        var coordinator = CreateCoordinator(
            recover: async (_, _) =>
            {
                recoveryEntered.TrySetResult();
                await releaseRecovery.Task;
            },
            start: _ =>
            {
                Interlocked.Increment(ref starts);
                return Task.CompletedTask;
            },
            appLifetime: lifetime.Token);
        await using (coordinator)
        {
            var recovery = coordinator.RecoverAndRefreshAsync(CancellationToken.None);
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            lifetime.Cancel();
            releaseRecovery.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        }

        Assert.Equal(0, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task Concurrent_recovery_requests_are_serialized()
    {
        var firstRecoveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRecovery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRecoveries = 0;
        var maximumActiveRecoveries = 0;
        var recoveryCalls = 0;
        var coordinator = CreateCoordinator(recover: async (_, _) =>
        {
            var active = Interlocked.Increment(ref activeRecoveries);
            UpdateMaximum(ref maximumActiveRecoveries, active);
            if (Interlocked.Increment(ref recoveryCalls) == 1)
            {
                firstRecoveryEntered.TrySetResult();
                await releaseFirstRecovery.Task;
            }
            Interlocked.Decrement(ref activeRecoveries);
        });
        await using (coordinator)
        {
            var first = coordinator.RecoverAndRefreshAsync(CancellationToken.None);
            await firstRecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var second = coordinator.RecoverAndRefreshAsync(CancellationToken.None);

            releaseFirstRecovery.TrySetResult();
            await Task.WhenAll(first, second);
        }

        Assert.Equal(2, Volatile.Read(ref recoveryCalls));
        Assert.Equal(1, Volatile.Read(ref maximumActiveRecoveries));
    }

    [Fact]
    public async Task Disposal_rejects_new_work_and_awaits_admitted_recovery()
    {
        var recoveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(recover: async (_, _) =>
        {
            recoveryEntered.TrySetResult();
            await releaseRecovery.Task;
        });

        var admitted = coordinator.RecoverAndRefreshAsync(CancellationToken.None);
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disposal = coordinator.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.RecoverAndRefreshAsync(CancellationToken.None));
        releaseRecovery.TrySetResult();
        await admitted;
        await disposal;
    }

    private static ReminderRecoveryCoordinator CreateCoordinator(
        Func<CancellationToken, Task>? stop = null,
        Func<DateTimeOffset, CancellationToken, Task>? recover = null,
        Func<CancellationToken, Task>? start = null,
        Func<CancellationToken, Task>? refresh = null,
        CancellationToken appLifetime = default) =>
        new(
            stop ?? (_ => Task.CompletedTask),
            recover ?? ((_, _) => Task.CompletedTask),
            start ?? (_ => Task.CompletedTask),
            refresh ?? (_ => Task.CompletedTask),
            new FakeClock(Now),
            appLifetime);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (observed < value)
        {
            var original = Interlocked.CompareExchange(ref maximum, value, observed);
            if (original == observed)
                return;
            observed = original;
        }
    }
}
