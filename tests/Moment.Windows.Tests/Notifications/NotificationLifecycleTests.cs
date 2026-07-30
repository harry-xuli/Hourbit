using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Windows.Notifications;

namespace Moment.Windows.Tests.Notifications;

public sealed class NotificationLifecycleTests
{
    [Fact]
    public async Task Router_separates_button_actions_from_navigation_and_rejects_malformed_input()
    {
        var source = new FakeActivationSource();
        var actions = new RecordingActions();
        var navigation = new RecordingNavigation();
        await using var router = new NotificationActivationRouter(source, actions, navigation);
        router.Start();

        await source.RaiseAsync("section=missed");
        await source.RaiseAsync("action=complete&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d");
        await source.RaiseAsync("section=unknown");

        Assert.Equal(["missed"], navigation.Calls);
        Assert.Equal(["complete:4b3eb3c9-970d-47d7-89e2-bab9778a406d"], actions.Calls);
        Assert.Equal(1, source.RegisterCount);
        await router.DisposeAsync();
        Assert.Equal(1, source.UnregisterCount);
    }

    [Fact]
    public async Task Sink_publishes_refreshed_notification_health()
    {
        var platform = new MutablePlatform(NotificationHealth.Available);
        var sink = new AppNotificationSink(platform, new NoopAlerts(), new RecordingActions());
        var observed = new List<NotificationHealth>();
        sink.HealthChanged += observed.Add;

        platform.Set(NotificationHealth.PermissionDisabled);
        await sink.RefreshHealthAsync(CancellationToken.None);

        Assert.Equal(NotificationHealth.PermissionDisabled, sink.Health);
        Assert.Equal([NotificationHealth.PermissionDisabled], observed);
    }

    private sealed class FakeActivationSource : INotificationActivationSource
    {
        public event Func<string, Task>? Invoked;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public void Register() => RegisterCount++;
        public void Unregister() => UnregisterCount++;
        public async Task RaiseAsync(string value) { if (Invoked is { } invoked) foreach (Func<string, Task> handler in invoked.GetInvocationList()) await handler(value); }
    }
    private sealed class RecordingNavigation : INotificationNavigator { public List<string> Calls { get; }=[]; public Task NavigateAsync(NotificationNavigation navigation, CancellationToken ct) { Calls.Add(navigation.Section); return Task.CompletedTask; } }
    private sealed class NoopAlerts : IImportantAlertDelivery { public Task EnqueueAsync(ReminderAlert alert, CancellationToken ct) => Task.CompletedTask; }
    private sealed class MutablePlatform(NotificationHealth health) : INotificationPlatform, INotificationHealthSource
    { public NotificationHealth Health { get; private set; }=health; public event Action<NotificationHealth>? HealthChanged; public void Set(NotificationHealth value) { Health=value; HealthChanged?.Invoke(value); } public Task RefreshHealthAsync(CancellationToken ct)=>Task.CompletedTask; public Task ShowAsync(NotificationPayload payload,CancellationToken ct)=>Task.CompletedTask; public Task OpenSettingsAsync(CancellationToken ct)=>Task.CompletedTask; }
    private sealed class RecordingActions : IReminderActionService { public List<string> Calls { get; }=[]; public Task CompleteAsync(Guid id,CancellationToken ct){Calls.Add("complete:"+id);return Task.CompletedTask;} public Task IgnoreAsync(Guid id,CancellationToken ct){Calls.Add("ignore:"+id);return Task.CompletedTask;} public Task<ReminderOccurrence> SnoozeAsync(Guid id,TimeSpan delay,CancellationToken ct)=>Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(),DateTimeOffset.UtcNow)); }
}
