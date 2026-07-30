using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Windows.Alerts;
using Moment.Windows.Notifications;

namespace Moment.Windows.Tests.Runtime;

public sealed class WindowsRuntimeTests
{
    [Fact]
    public async Task Runtime_starts_once_routes_activation_and_unsubscribes_on_dispose()
    {
        var source = new Source(); var actions = new Actions(); var navigation = new Navigation();
        await using var runtime = new WindowsNotificationRuntime(source, actions, navigation);
        runtime.Start(); runtime.Start();
        await source.Raise("occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&action=complete");
        await source.Raise("section=missed");
        Assert.Equal(1, source.Registers); Assert.Equal(["complete"], actions.Calls); Assert.Equal(["missed"], navigation.Calls);
        await runtime.DisposeAsync(); await runtime.DisposeAsync();
        Assert.Equal(1, source.Unregisters);
    }

    [Fact]
    public async Task Start_racing_dispose_cannot_leave_an_activation_subscription()
    {
        var source = new ControllableSource(blockRegister: true);
        var actions = new Actions();
        var navigation = new Navigation();
        var runtime = new WindowsNotificationRuntime(source, actions, navigation);

        var start = Task.Run(runtime.Start);
        Assert.True(source.RegisterEntered.Wait(TimeSpan.FromSeconds(5)));
        var dispose = Task.Run(async () => await runtime.DisposeAsync());
        source.ReleaseRegister();

        await Task.WhenAll(start, dispose);
        await source.Raise("action=complete&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d");

        Assert.Equal(1, source.Registers);
        Assert.Equal(1, source.Unregisters);
        Assert.False(source.HasSubscribers);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task Concurrent_dispose_callers_all_wait_for_unsubscription_completion()
    {
        var source = new ControllableSource(blockUnregister: true);
        var actions = new Actions();
        var runtime = new WindowsNotificationRuntime(source, actions, new Navigation());
        runtime.Start();

        var firstDispose = Task.Run(async () => await runtime.DisposeAsync());
        Assert.True(source.UnregisterEntered.Wait(TimeSpan.FromSeconds(5)));
        var secondDispose = runtime.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        Assert.Equal(1, source.Registers);
        Assert.Equal(1, source.Unregisters);

        source.ReleaseUnregister();
        await Task.WhenAll(firstDispose, secondDispose);
        await source.Raise("action=complete&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d");

        Assert.Throws<ObjectDisposedException>(runtime.Start);
        Assert.Equal(1, source.Registers);
        Assert.Equal(1, source.Unregisters);
        Assert.False(source.HasSubscribers);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task Production_controller_factory_uses_looping_audio_adapter()
    {
        var player = new Player();
        await using var controller = ImportantAlertControllerFactory.Create(new Presenter(), new Actions(), player);
        await controller.EnqueueAsync(new ReminderAlert(Guid.NewGuid(), "A", DateTimeOffset.UtcNow), default);
        Assert.Equal(1, player.Starts);
    }

    private sealed class Source : INotificationActivationSource { public event Func<string,Task>? Invoked; public int Registers; public int Unregisters; public void Register()=>Registers++; public void Unregister()=>Unregisters++; public async Task Raise(string s){if(Invoked is { } e)foreach(Func<string,Task> h in e.GetInvocationList())await h(s);} }
    private sealed class ControllableSource(bool blockRegister = false, bool blockUnregister = false) : INotificationActivationSource
    {
        private readonly ManualResetEventSlim _registerRelease = new(!blockRegister);
        private readonly ManualResetEventSlim _unregisterRelease = new(!blockUnregister);
        private Func<string, Task>? _invoked;
        public event Func<string, Task>? Invoked { add => _invoked += value; remove => _invoked -= value; }
        public ManualResetEventSlim RegisterEntered { get; } = new(false);
        public ManualResetEventSlim UnregisterEntered { get; } = new(false);
        public int Registers;
        public int Unregisters;
        public bool HasSubscribers => _invoked is not null;
        public void Register()
        {
            Interlocked.Increment(ref Registers);
            RegisterEntered.Set();
            _registerRelease.Wait();
        }
        public void Unregister()
        {
            Interlocked.Increment(ref Unregisters);
            UnregisterEntered.Set();
            _unregisterRelease.Wait();
        }
        public void ReleaseRegister() => _registerRelease.Set();
        public void ReleaseUnregister() => _unregisterRelease.Set();
        public async Task Raise(string arguments)
        {
            if (_invoked is { } invoked)
                foreach (Func<string, Task> handler in invoked.GetInvocationList())
                    await handler(arguments);
        }
    }
    private sealed class Actions : IReminderActionService { public List<string> Calls {get;}=[];public Task CompleteAsync(Guid x,CancellationToken c){Calls.Add("complete");return Task.CompletedTask;}public Task IgnoreAsync(Guid x,CancellationToken c){Calls.Add("ignore");return Task.CompletedTask;}public Task<ReminderOccurrence>SnoozeAsync(Guid x,TimeSpan d,CancellationToken c)=>Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(),DateTimeOffset.UtcNow)); }
    private sealed class Navigation : INotificationNavigator { public List<string> Calls {get;}=[]; public Task NavigateAsync(NotificationNavigation n,CancellationToken c){Calls.Add(n.Section);return Task.CompletedTask;} }
    private sealed class Presenter : IImportantAlertPresenter { public Task<ImportantAlertAction> ShowAsync(ReminderAlert a,CancellationToken c)=>Task.FromResult(ImportantAlertAction.Ignore); }
    private sealed class Player : ILoopingAudioPlayer { public int Starts;public Task StartLoopAsync(Stream s,CancellationToken c){Starts++;return Task.CompletedTask;}public Task StopAsync(CancellationToken c)=>Task.CompletedTask; }
}
