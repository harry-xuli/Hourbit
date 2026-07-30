using Moment.Windows.Lifecycle;

namespace Moment.Windows.Tests.Lifecycle;

public sealed class SystemResumeMonitorTests
{
    [Fact]
    public void Debounce_window_is_exactly_500_milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), SystemResumeMonitor.DebounceWindow);
    }

    [Fact]
    public void Windows_source_requests_broadcast_capable_window_and_maps_all_native_reasons()
    {
        var factory = new LifecycleWindowFactory();
        using var source = new WindowsSystemResumeEventSource(factory);
        var reasons = new List<ResumeReason>();
        source.Resumed += (_, reason) => reasons.Add(reason);

        source.Start();
        factory.Window.Raise(NativeLifecycleReason.Unlock);
        factory.Window.Raise(NativeLifecycleReason.PowerResume);
        factory.Window.Raise(NativeLifecycleReason.TimeChanged);
        factory.Window.Raise(NativeLifecycleReason.TimeZoneChanged);
        source.Stop();

        Assert.Equal(LifecycleWindowMode.HiddenTopLevel, factory.RequestedMode);
        Assert.Equal(
            [
                ResumeReason.Unlock,
                ResumeReason.PowerResume,
                ResumeReason.TimeChanged,
                ResumeReason.TimeZoneChanged
            ],
            reasons);
        Assert.Equal(1, factory.Window.Starts);
        Assert.Equal(1, factory.Window.Stops);
    }

    [Fact]
    public async Task Lifecycle_burst_is_debounced_to_one_recovery_with_latest_reason()
    {
        var source = new Source();
        var delay = new Delay();
        var reasons = new List<ResumeReason>();
        await using var monitor = new SystemResumeMonitor(
            source,
            (reason, _) => { reasons.Add(reason); return Task.CompletedTask; },
            delay);
        monitor.Start();
        monitor.Start();

        source.Raise(ResumeReason.Unlock);
        await delay.WaitForCallsAsync(1);
        source.Raise(ResumeReason.TimeZoneChanged);
        await delay.WaitForCallsAsync(2);
        delay.Release(1);
        await EventuallyAsync(() => reasons.Count == 1);

        Assert.Equal([ResumeReason.TimeZoneChanged], reasons);
        Assert.Equal(1, source.Starts);
    }

    [Fact]
    public async Task Disposal_cancels_pending_debounce_stops_source_and_blocks_later_events()
    {
        var source = new Source();
        var delay = new Delay();
        var calls = 0;
        var monitor = new SystemResumeMonitor(source, (_, _) => { calls++; return Task.CompletedTask; }, delay);
        monitor.Start();
        source.Raise(ResumeReason.PowerResume);
        await delay.WaitForCallsAsync(1);

        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
        source.Raise(ResumeReason.Unlock);
        delay.Release(0);
        await Task.Delay(20);

        Assert.Equal(0, calls);
        Assert.Equal(1, source.Stops);
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    [Fact]
    public async Task Recovery_callback_can_await_reentrant_disposal_without_self_deadlock()
    {
        var source = new Source();
        var delay = new Delay();
        SystemResumeMonitor? monitor = null;
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor = new SystemResumeMonitor(source, async (_, _) =>
        {
            await monitor!.DisposeAsync();
            callbackFinished.TrySetResult();
        }, delay);
        monitor.Start();

        source.Raise(ResumeReason.Unlock);
        await delay.WaitForCallsAsync(1);
        delay.Release(0);

        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await monitor.DisposeAsync();
        Assert.Equal(1, source.Stops);
    }

    [Fact]
    public async Task External_disposal_waits_for_an_in_flight_recovery_callback()
    {
        var source = new Source();
        var delay = new Delay();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new SystemResumeMonitor(source, async (_, _) =>
        {
            callbackEntered.TrySetResult();
            await callbackRelease.Task;
        }, delay);
        monitor.Start();
        source.Raise(ResumeReason.PowerResume);
        await delay.WaitForCallsAsync(1);
        delay.Release(0);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposal = monitor.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        callbackRelease.TrySetResult();

        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, source.Stops);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class Source : ISystemResumeEventSource
    {
        public event EventHandler<ResumeReason>? Resumed;
        public int Starts;
        public int Stops;
        public void Start() => Starts++;
        public void Stop() => Stops++;
        public void Raise(ResumeReason reason) => Resumed?.Invoke(this, reason);
        public void Dispose() { }
    }

    private sealed class Delay : IResumeDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _calls = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _calls.Add(completion);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }
        public void Release(int index) { lock (_gate) _calls[index].TrySetResult(); }
        public async Task WaitForCallsAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                lock (_gate) if (_calls.Count >= count) return;
                if (DateTime.UtcNow >= timeout) throw new TimeoutException();
                await Task.Delay(10);
            }
        }
    }

    private sealed class LifecycleWindowFactory : ILifecycleNativeWindowFactory
    {
        public LifecycleWindowMode RequestedMode { get; private set; }
        public LifecycleWindow Window { get; } = new();
        public ILifecycleNativeWindow Create(LifecycleWindowMode mode)
        {
            RequestedMode = mode;
            return Window;
        }
    }

    private sealed class LifecycleWindow : ILifecycleNativeWindow
    {
        public event EventHandler<NativeLifecycleReason>? Signaled;
        public int Starts;
        public int Stops;
        public void Start() => Starts++;
        public void Stop() => Stops++;
        public void Raise(NativeLifecycleReason reason) => Signaled?.Invoke(this, reason);
        public void Dispose() { }
    }
}
