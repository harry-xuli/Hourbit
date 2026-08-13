namespace Hourbit.App.Tests;

public sealed class WpfTestHostTests
{
    [Fact]
    public async Task Async_dispatcher_action_is_awaited_before_RunAsync_completes()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = false;

        var run = WpfTestHost.RunAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            resumed = true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(run.IsCompleted);
        release.TrySetResult();
        await run;
        Assert.True(resumed);
    }
}
