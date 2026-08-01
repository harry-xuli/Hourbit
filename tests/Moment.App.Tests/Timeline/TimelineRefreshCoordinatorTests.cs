using Moment.App.Timeline;

namespace Moment.App.Tests.Timeline;

public sealed class TimelineRefreshCoordinatorTests
{
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
