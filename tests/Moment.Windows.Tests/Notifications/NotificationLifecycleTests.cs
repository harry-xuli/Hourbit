using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Windows.Notifications;

namespace Moment.Windows.Tests.Notifications;

public sealed class NotificationLifecycleTests
{
    private static readonly Guid OccurrenceId = Guid.Parse("4b3eb3c9-970d-47d7-89e2-bab9778a406d");

    [Fact]
    public async Task Router_separates_button_actions_from_navigation_and_rejects_malformed_input()
    {
        var source = new FakeActivationSource();
        var actions = new RecordingActions();
        var navigation = new RecordingNavigation();
        await using var router = new NotificationActivationRouter(source, actions, navigation);
        router.Start();

        await source.RaiseAsync("section=missed");
        await source.RaiseAsync("occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&section=timeline");
        await source.RaiseAsync("action=complete&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d");
        await source.RaiseAsync("section=unknown");

        Assert.Equal(["missed", "timeline"], navigation.Calls);
        Assert.Equal(["complete:4b3eb3c9-970d-47d7-89e2-bab9778a406d"], actions.Calls);
        Assert.Equal(1, source.RegisterCount);
        await router.DisposeAsync();
        Assert.Equal(1, source.UnregisterCount);
    }

    [Theory]
    [InlineData("section=timeline&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d")]
    [InlineData("occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&section=timeline")]
    public async Task Timeline_navigation_accepts_only_the_exact_pair_in_either_order(string arguments)
    {
        var source = new FakeActivationSource();
        var actions = new RecordingActions();
        var navigation = new RecordingNavigation();
        await using var router = new NotificationActivationRouter(source, actions, navigation);
        router.Start();

        await source.RaiseAsync(arguments);

        Assert.Equal([new NotificationNavigation("timeline", OccurrenceId)], navigation.Navigations);
        Assert.Empty(actions.Calls);
    }

    [Theory]
    [InlineData("section=missed&section=missed")]
    [InlineData("section=missed&unknown")]
    [InlineData("section=missed&")]
    [InlineData("section=missed&&unknown=value")]
    [InlineData("section=missed&unknown=value")]
    [InlineData("section=missed&extra=value")]
    [InlineData("section=missed%ZZ")]
    [InlineData("section=timeline&occurrenceId=not-a-guid")]
    [InlineData("section=timeline&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&extra=value")]
    [InlineData("occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&section=timeline")]
    public async Task Invalid_navigation_is_ignored_without_throwing_or_routing(string arguments)
    {
        var source = new FakeActivationSource();
        var actions = new RecordingActions();
        var navigation = new RecordingNavigation();
        await using var router = new NotificationActivationRouter(source, actions, navigation);
        router.Start();

        var exception = await Record.ExceptionAsync(() => source.RaiseAsync(arguments));

        Assert.Null(exception);
        Assert.Empty(navigation.Navigations);
        Assert.Empty(actions.Calls);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_failures_including_unauthorized_stay_failed_until_retry_then_setting_maps_health(bool unauthorized)
    {
        var client = new WindowsClient
        {
            RegisterException = unauthorized
                ? new UnauthorizedAccessException()
                : new InvalidOperationException("register failed"),
            IsEnabled = false
        };
        var platform = new WindowsAppNotificationPlatform(client);
        var observed = new List<NotificationHealth>();
        platform.HealthChanged += observed.Add;

        Assert.Equal(NotificationHealth.RegistrationFailed, platform.Health);
        await platform.RefreshHealthAsync(default);
        Assert.Equal(NotificationHealth.RegistrationFailed, platform.Health);
        Assert.Empty(observed);

        client.RegisterException = null;
        await platform.RefreshHealthAsync(default);
        Assert.Equal(NotificationHealth.PermissionDisabled, platform.Health);
        client.IsEnabled = true;
        await platform.RefreshHealthAsync(default);
        Assert.Equal(NotificationHealth.Available, platform.Health);
        Assert.Equal(
            [NotificationHealth.PermissionDisabled, NotificationHealth.Available],
            observed);
        Assert.Equal(3, client.RegisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_and_test_show_failures_close_registration_gate_until_refresh_reregisters(bool testNotification)
    {
        var client = new WindowsClient { IsEnabled = true };
        var platform = new WindowsAppNotificationPlatform(client);
        var sink = new AppNotificationSink(platform, new NoopAlerts(), new RecordingActions());
        var observed = new List<NotificationHealth>();
        platform.HealthChanged += observed.Add;
        client.ShowException = new InvalidOperationException("show failed");

        if (testNotification)
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.SendTestNotificationAsync(default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sink.DeliverAsync(Moment.TestSupport.TestData.Scheduled("Normal", "2026-08-01T09:30:00+08:00"), default));

        Assert.Equal(NotificationHealth.RegistrationFailed, platform.Health);
        Assert.Equal([NotificationHealth.RegistrationFailed], observed);
        Assert.Equal(1, client.ShowCount);

        client.ShowException = null;
        await sink.RefreshHealthAsync(default);
        await sink.RefreshHealthAsync(default);

        Assert.Equal(NotificationHealth.Available, platform.Health);
        Assert.Equal(
            [NotificationHealth.RegistrationFailed, NotificationHealth.Available],
            observed);
        Assert.Equal(2, client.RegisterCount);
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
    private sealed class RecordingNavigation : INotificationNavigator
    {
        public List<NotificationNavigation> Navigations { get; } = [];
        public IEnumerable<string> Calls => Navigations.Select(navigation => navigation.Section);
        public Task NavigateAsync(NotificationNavigation navigation, CancellationToken ct)
        {
            Navigations.Add(navigation);
            return Task.CompletedTask;
        }
    }
    private sealed class NoopAlerts : IImportantAlertDelivery { public Task EnqueueAsync(ReminderAlert alert, CancellationToken ct) => Task.CompletedTask; }
    private sealed class MutablePlatform(NotificationHealth health) : INotificationPlatform, INotificationHealthSource
    { public NotificationHealth Health { get; private set; }=health; public event Action<NotificationHealth>? HealthChanged; public void Set(NotificationHealth value) { Health=value; HealthChanged?.Invoke(value); } public Task RefreshHealthAsync(CancellationToken ct)=>Task.CompletedTask; public Task ShowAsync(NotificationPayload payload,CancellationToken ct)=>Task.CompletedTask; public Task OpenSettingsAsync(CancellationToken ct)=>Task.CompletedTask; }
    private sealed class WindowsClient : IWindowsNotificationClient
    {
        public bool IsEnabled { get; set; }
        public Exception? RegisterException { get; set; }
        public Exception? ShowException { get; set; }
        public int RegisterCount { get; private set; }
        public int ShowCount { get; private set; }
        public void Register()
        {
            RegisterCount++;
            if (RegisterException is not null) throw RegisterException;
        }
        public void Show(NotificationPayload payload)
        {
            ShowCount++;
            if (ShowException is not null) throw ShowException;
        }
    }
    private sealed class RecordingActions : IReminderActionService { public List<string> Calls { get; }=[]; public Task CompleteAsync(Guid id,CancellationToken ct){Calls.Add("complete:"+id);return Task.CompletedTask;} public Task IgnoreAsync(Guid id,CancellationToken ct){Calls.Add("ignore:"+id);return Task.CompletedTask;} public Task<ReminderOccurrence> SnoozeAsync(Guid id,TimeSpan delay,CancellationToken ct)=>Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(),DateTimeOffset.UtcNow)); }
}
