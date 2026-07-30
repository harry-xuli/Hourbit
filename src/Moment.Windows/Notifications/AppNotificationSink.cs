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

public interface IImportantAlertDelivery
{
    Task EnqueueAsync(ReminderAlert alert, CancellationToken ct);
}

/// <summary>Windows App SDK boundary; tests use <see cref="INotificationPlatform"/> fakes instead.</summary>
public sealed class WindowsAppNotificationPlatform : INotificationPlatform
{
    public NotificationHealth Health { get; private set; } = NotificationHealth.Available;

    public WindowsAppNotificationPlatform()
    {
        try
        {
            AppNotificationManager.Default.Register();
        }
        catch (UnauthorizedAccessException)
        {
            Health = NotificationHealth.PermissionDisabled;
        }
        catch
        {
            Health = NotificationHealth.RegistrationFailed;
        }
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
            var notification = new AppNotification(payload.ToXml()) { Tag = payload.Tag, Group = payload.Group };
            AppNotificationManager.Default.Show(notification);
            return Task.CompletedTask;
        }
        catch (UnauthorizedAccessException)
        {
            Health = NotificationHealth.PermissionDisabled;
            throw;
        }
        catch
        {
            Health = NotificationHealth.RegistrationFailed;
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

public sealed class AppNotificationSink(INotificationPlatform platform, IImportantAlertDelivery importantAlerts, IReminderActionService actions) : IReminderSink
{
    public NotificationHealth Health => platform.Health;
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
