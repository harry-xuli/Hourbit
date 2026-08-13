using Hourbit.App.Timeline;

namespace Hourbit.App.Tests.Timeline;

public sealed class TimelineRefreshCoordinatorTests
{
    [Fact]
    public async Task Pre_cancelled_owned_drain_rethrows_without_starting_reload()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var reloads = 0;
            var coordinator = new TimelineRefreshCoordinator(
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                () =>
                {
                    Interlocked.Increment(ref reloads);
                    return Task.CompletedTask;
                });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => coordinator.DrainAsync(cancellation.Token));

            Assert.Equal(0, Volatile.Read(ref reloads));
            await coordinator.DisposeAsync();
        });
    }

    [Fact]
    public async Task Disposal_after_waiter_cancellation_drains_active_and_trailing_reloads()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var firstReloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var trailingReloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseTrailingReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                if (Interlocked.Increment(ref reloads) == 1)
                {
                    firstReloadEntered.TrySetResult();
                    await releaseFirstReload.Task;
                }
                else
                {
                    trailingReloadEntered.TrySetResult();
                    await releaseTrailingReload.Task;
                }
            });
            using var firstCancellation = new CancellationTokenSource();
            using var trailingCancellation = new CancellationTokenSource();

            var first = coordinator.RequestAsync(firstCancellation.Token);
            await firstReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var trailing = coordinator.RequestAsync(trailingCancellation.Token);
            firstCancellation.Cancel();
            trailingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trailing);

            var disposal = coordinator.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            releaseFirstReload.TrySetResult();
            await trailingReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(disposal.IsCompleted);
            releaseTrailingReload.TrySetResult();

            await disposal.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, Volatile.Read(ref reloads));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => coordinator.RequestAsync(CancellationToken.None));
        });
    }

    [Fact]
    public async Task Disposal_observes_reload_failure_after_waiter_cancellation()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new InvalidOperationException("late refresh failure");
            var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task;
                throw expected;
            });
            using var cancellation = new CancellationTokenSource();

            var request = coordinator.RequestAsync(cancellation.Token);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            var disposal = coordinator.DisposeAsync().AsTask();
            releaseReload.TrySetResult();

            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => disposal.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Same(expected, observed);
        });
    }

    [Fact]
    public async Task Dispatcher_shutdown_during_active_reload_completes_request_and_disposal_without_deadlock()
    {
        var started = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            started.TrySetResult(dispatcher);
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var dispatcher = await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var reloadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
        {
            reloadEntered.TrySetResult();
            await releaseReload.Task;
        });
        Task? request = null;
        await dispatcher.InvokeAsync(
            () => request = coordinator.RequestAsync(CancellationToken.None));
        await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        dispatcher.BeginInvokeShutdown(
            System.Windows.Threading.DispatcherPriority.Send);
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
        releaseReload.TrySetResult();

        var requestFailure = await Record.ExceptionAsync(
            () => request!.WaitAsync(TimeSpan.FromSeconds(1)));
        var disposalFailure = await Record.ExceptionAsync(
            () => coordinator.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.False(requestFailure is TimeoutException);
        Assert.False(disposalFailure is TimeoutException);
    }

    [Fact]
    public async Task Requests_during_an_active_reload_coalesce_to_one_trailing_dispatcher_reload()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var firstReloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            var allReloadsWereDispatcherAffine = true;
            var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                allReloadsWereDispatcherAffine &= dispatcher.CheckAccess();
                var reload = Interlocked.Increment(ref reloads);
                if (reload == 1)
                {
                    firstReloadEntered.TrySetResult();
                    await releaseFirstReload.Task;
                }
            });

            var first = coordinator.RequestAsync(CancellationToken.None);
            await firstReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var second = coordinator.RequestAsync(CancellationToken.None);
            var third = coordinator.RequestAsync(CancellationToken.None);

            Assert.Equal(1, Volatile.Read(ref reloads));
            releaseFirstReload.TrySetResult();
            await Task.WhenAll(first, second, third);

            Assert.Equal(2, Volatile.Read(ref reloads));
            Assert.True(allReloadsWereDispatcherAffine);
        });
    }

    [Fact]
    public async Task Cancellation_of_a_waiter_does_not_cancel_the_admitted_reload()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reloadCompleted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task;
                reloadCompleted.TrySetResult();
            });
            using var cancellation = new CancellationTokenSource();

            var request = coordinator.RequestAsync(cancellation.Token);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            releaseReload.TrySetResult();
            await reloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public async Task A_reload_failure_is_observed_by_every_coalesced_request()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reloadEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new TimelineRefreshCoordinator(dispatcher, async () =>
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task;
                throw new InvalidOperationException("timeline failed");
            });

            var first = coordinator.RequestAsync(CancellationToken.None);
            await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var second = coordinator.RequestAsync(CancellationToken.None);
            releaseReload.TrySetResult();

            var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal("timeline failed", firstFailure.Message);
            Assert.Equal("timeline failed", secondFailure.Message);
        });
    }
}
