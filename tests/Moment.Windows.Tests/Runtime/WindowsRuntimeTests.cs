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
    public async Task Production_controller_factory_uses_looping_audio_adapter()
    {
        var player = new Player();
        await using var controller = ImportantAlertControllerFactory.Create(new Presenter(), new Actions(), player);
        await controller.EnqueueAsync(new ReminderAlert(Guid.NewGuid(), "A", DateTimeOffset.UtcNow), default);
        Assert.Equal(1, player.Starts);
    }

    private sealed class Source : INotificationActivationSource { public event Func<string,Task>? Invoked; public int Registers; public int Unregisters; public void Register()=>Registers++; public void Unregister()=>Unregisters++; public async Task Raise(string s){if(Invoked is { } e)foreach(Func<string,Task> h in e.GetInvocationList())await h(s);} }
    private sealed class Actions : IReminderActionService { public List<string> Calls {get;}=[];public Task CompleteAsync(Guid x,CancellationToken c){Calls.Add("complete");return Task.CompletedTask;}public Task IgnoreAsync(Guid x,CancellationToken c){Calls.Add("ignore");return Task.CompletedTask;}public Task<ReminderOccurrence>SnoozeAsync(Guid x,TimeSpan d,CancellationToken c)=>Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(),DateTimeOffset.UtcNow)); }
    private sealed class Navigation : INotificationNavigator { public List<string> Calls {get;}=[]; public Task NavigateAsync(NotificationNavigation n,CancellationToken c){Calls.Add(n.Section);return Task.CompletedTask;} }
    private sealed class Presenter : IImportantAlertPresenter { public Task<ImportantAlertAction> ShowAsync(ReminderAlert a,CancellationToken c)=>Task.FromResult(ImportantAlertAction.Ignore); }
    private sealed class Player : ILoopingAudioPlayer { public int Starts;public Task StartLoopAsync(Stream s,CancellationToken c){Starts++;return Task.CompletedTask;}public Task StopAsync(CancellationToken c)=>Task.CompletedTask; }
}
