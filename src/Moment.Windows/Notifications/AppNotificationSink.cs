using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Services;
using System.Diagnostics;
using System.Security;
using Microsoft.Windows.AppNotifications;

namespace Moment.Windows.Notifications;

public enum NotificationHealth { Available, PermissionDisabled, RegistrationFailed }
public sealed record NotificationButton(string Content, string Arguments);
public sealed record NotificationPayload(
    string Title,
    string Body,
    string Tag,
    string Group,
    string ActivationArguments,
    IReadOnlyList<NotificationButton> Buttons)
{
    public string ToXml()
    {
        var buttons = string.Concat(Buttons.Select(button =>
            $"<action content=\"{Escape(button.Content)}\" arguments=\"{Escape(button.Arguments)}\" />"));
        return $"<toast launch=\"{Escape(ActivationArguments)}\"><visual><binding template=\"ToastGeneric\"><text>{Escape(Title)}</text><text>{Escape(Body)}</text></binding></visual><actions>{buttons}</actions></toast>";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}

public interface INotificationPlatform
{
    NotificationHealth Health { get; }
    Task ShowAsync(NotificationPayload payload, CancellationToken ct);
    Task OpenSettingsAsync(CancellationToken ct);
}
public interface INotificationHealthSource
{
    event Action<NotificationHealth>? HealthChanged;
    Task RefreshHealthAsync(CancellationToken ct);
}
public interface IWindowsNotificationClient
{
    void Register();
    bool IsEnabled { get; }
    void Show(NotificationPayload payload);
}

public interface INotificationActivationSource
{
    event Func<string, Task>? Invoked;
    void Register();
    void Unregister();
}

public sealed record NotificationNavigation(string Section, Guid? OccurrenceId);
public interface INotificationNavigator { Task NavigateAsync(NotificationNavigation navigation, CancellationToken ct); }

public interface IImportantAlertDelivery
{
    Task EnqueueAsync(ReminderAlert alert, CancellationToken ct);
}

/// <summary>Windows App SDK boundary; tests use <see cref="INotificationPlatform"/> fakes instead.</summary>
public sealed class WindowsAppNotificationPlatform : INotificationPlatform, INotificationHealthSource
{
    public NotificationHealth Health { get; private set; } = NotificationHealth.Available;
    public event Action<NotificationHealth>? HealthChanged;
    private bool _registered;
    private readonly IWindowsNotificationClient _client;

    public WindowsAppNotificationPlatform(IWindowsNotificationClient? client = null)
    {
        _client = client ?? new WindowsAppNotificationClient();
        RefreshHealthAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task RefreshHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!_registered)
            {
                _client.Register();
                _registered = true;
            }
            SetHealth(_client.IsEnabled ? NotificationHealth.Available : NotificationHealth.PermissionDisabled);
        }
        catch { _registered = false; SetHealth(NotificationHealth.RegistrationFailed); }
        return Task.CompletedTask;
    }

    private void SetHealth(NotificationHealth health)
    {
        if (Health == health) return;
        Health = health;
        HealthChanged?.Invoke(health);
    }

    public Task ShowAsync(NotificationPayload payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Health != NotificationHealth.Available)
        {
            throw new InvalidOperationException($"Notifications are unavailable: {Health}.");
        }

        try
        {
            _client.Show(payload);
            return Task.CompletedTask;
        }
        catch
        {
            _registered = false;
            SetHealth(NotificationHealth.RegistrationFailed);
            throw;
        }
    }

    public Task OpenSettingsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

internal sealed class WindowsAppNotificationClient : IWindowsNotificationClient
{
    public void Register() => AppNotificationManager.Default.Register();
    public bool IsEnabled => AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
    public void Show(NotificationPayload payload)
    {
        var notification = new AppNotification(payload.ToXml()) { Tag = payload.Tag, Group = payload.Group };
        AppNotificationManager.Default.Show(notification);
    }
}

public sealed class AppNotificationSink(INotificationPlatform platform, IImportantAlertDelivery importantAlerts, IReminderActionService actions) : IReminderSink
{
    public event Action<NotificationHealth>? HealthChanged
    {
        add { if (platform is INotificationHealthSource source) source.HealthChanged += value; }
        remove { if (platform is INotificationHealthSource source) source.HealthChanged -= value; }
    }
    public NotificationHealth Health => platform.Health;
    public Task RefreshHealthAsync(CancellationToken ct) => platform is INotificationHealthSource source ? source.RefreshHealthAsync(ct) : Task.CompletedTask;
    public Task OpenWindowsNotificationSettingsAsync(CancellationToken ct) => platform.OpenSettingsAsync(ct);

    public Task SendTestNotificationAsync(CancellationToken ct) => platform.ShowAsync(new NotificationPayload(
        "时刻", "这是一条测试通知。", "moment-test", "moment-reminders", "section=timeline", []), ct);

    public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct)
    {
        if (reminder.Item.Importance == ReminderImportance.Important)
        {
            return importantAlerts.EnqueueAsync(ReminderAlert.From(reminder), ct);
        }

        var id = reminder.Occurrence.Id;
        return platform.ShowAsync(new NotificationPayload(
            reminder.Item.Title,
            $"计划时间：{reminder.Occurrence.DueAt:HH:mm}",
            id.ToString("D"),
            "moment-reminders",
            "section=timeline&occurrenceId=" + id.ToString("D"),
            [
                new NotificationButton("完成", NotificationArguments.Format(id, NotificationAction.Complete)),
                new NotificationButton("10 分钟后提醒", NotificationArguments.Format(id, NotificationAction.Snooze10)),
                new NotificationButton("忽略", NotificationArguments.Format(id, NotificationAction.Ignore))
            ]), ct);
    }

    public Task DeliverMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)
    {
        if (reminders.Count == 0)
        {
            return Task.CompletedTask;
        }

        var titles = string.Join("、", reminders.Take(3).Select(reminder => reminder.Item.Title));
        return platform.ShowAsync(new NotificationPayload(
            $"{reminders.Count} 个提醒已错过", titles, "missed-summary", "moment-reminders", "section=missed", []), ct);
    }

    public async Task<bool> HandleActivationAsync(string? arguments, CancellationToken ct)
    {
        if (!NotificationArguments.TryParse(arguments, out var activation))
        {
            return false;
        }

        switch (activation.Action)
        {
            case NotificationAction.Complete:
                await actions.CompleteAsync(activation.OccurrenceId, ct).ConfigureAwait(false);
                break;
            case NotificationAction.Snooze10:
                await actions.SnoozeAsync(activation.OccurrenceId, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                break;
            case NotificationAction.Ignore:
                await actions.IgnoreAsync(activation.OccurrenceId, ct).ConfigureAwait(false);
                break;
            default:
                return false;
        }

        return true;
    }
}

public sealed class NotificationActivationRouter(INotificationActivationSource source, IReminderActionService actions, INotificationNavigator navigator) : IAsyncDisposable
{
    public void Start() { source.Register(); source.Invoked += HandleAsync; }
    public ValueTask DisposeAsync() { source.Invoked -= HandleAsync; source.Unregister(); return ValueTask.CompletedTask; }
    private async Task HandleAsync(string arguments)
    {
        if (NotificationArguments.TryParse(arguments, out var action))
        {
            if (action.Action == NotificationAction.Complete) await actions.CompleteAsync(action.OccurrenceId, CancellationToken.None);
            else if (action.Action == NotificationAction.Ignore) await actions.IgnoreAsync(action.OccurrenceId, CancellationToken.None);
            else await actions.SnoozeAsync(action.OccurrenceId, TimeSpan.FromMinutes(10), CancellationToken.None);
            return;
        }

        if (TryParseNavigation(arguments, out var navigation))
            await navigator.NavigateAsync(navigation, CancellationToken.None);
    }

    private static bool TryParseNavigation(string? arguments, out NotificationNavigation navigation)
    {
        navigation = default!;
        if (string.IsNullOrEmpty(arguments)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in arguments.Split('&', StringSplitOptions.None))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator != segment.LastIndexOf('=') || separator == segment.Length - 1)
                return false;
            if (!values.TryAdd(segment[..separator], segment[(separator + 1)..]))
                return false;
        }

        if (values.Count == 1 && values.TryGetValue("section", out var missed) && missed == "missed")
        {
            navigation = new NotificationNavigation("missed", null);
            return true;
        }

        if (values.Count == 2 &&
            values.TryGetValue("section", out var section) && section == "timeline" &&
            values.TryGetValue("occurrenceId", out var occurrence) &&
            Guid.TryParseExact(occurrence, "D", out var occurrenceId))
        {
            navigation = new NotificationNavigation("timeline", occurrenceId);
            return true;
        }

        return false;
    }
}

/// <summary>Bridges the Windows App SDK activation lifetime into the testable router source.</summary>
public sealed class WindowsAppNotificationActivationSource : INotificationActivationSource
{
    public event Func<string, Task>? Invoked;
    public void Register() => AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
    public void Unregister() => AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var handlers = Invoked;
        if (handlers is null) return;
        foreach (Func<string, Task> handler in handlers.GetInvocationList())
        {
            _ = ObserveAsync(handler, string.Join("&", args.Arguments.Select(pair => pair.Key + "=" + pair.Value)));
        }
    }

    private static async Task ObserveAsync(Func<string, Task> handler, string arguments)
    {
        try { await handler(arguments).ConfigureAwait(false); }
        catch { }
    }
}
