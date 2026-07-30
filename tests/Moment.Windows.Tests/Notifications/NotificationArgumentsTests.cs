using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.TestSupport;
using Moment.Windows.Notifications;

namespace Moment.Windows.Tests.Notifications;

public sealed class NotificationArgumentsTests
{
    [Fact]
    public void Arguments_round_trip_occurrence_and_action()
    {
        var id = Guid.Parse("4b3eb3c9-970d-47d7-89e2-bab9778a406d");

        var parsed = NotificationArguments.Parse(NotificationArguments.Format(id, NotificationAction.Snooze10));

        Assert.Equal(id, parsed.OccurrenceId);
        Assert.Equal(NotificationAction.Snooze10, parsed.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("action=complete")]
    [InlineData("occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d")]
    [InlineData("action=unknown&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d")]
    [InlineData("action=complete&occurrenceId=not-a-guid")]
    [InlineData("action=complete&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d&extra=value")]
    [InlineData("action=complete&action=ignore&occurrenceId=4b3eb3c9-970d-47d7-89e2-bab9778a406d")]
    public async Task Invalid_activation_arguments_are_rejected_without_an_action(string arguments)
    {
        var actions = new RecordingActions();
        var sink = new AppNotificationSink(new RecordingNotificationPlatform(), new RecordingImportantAlerts(), actions);

        var handled = await sink.HandleActivationAsync(arguments, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task Normal_reminder_creates_metadata_and_three_action_buttons()
    {
        var platform = new RecordingNotificationPlatform();
        var sink = new AppNotificationSink(platform, new RecordingImportantAlerts(), new RecordingActions());
        var reminder = TestData.Scheduled("Call team", "2026-08-01T09:30:00+08:00");

        await sink.DeliverAsync(reminder, CancellationToken.None);

        var payload = Assert.Single(platform.Payloads);
        Assert.Equal(reminder.Occurrence.Id.ToString("D"), payload.Tag);
        Assert.Equal("moment-reminders", payload.Group);
        Assert.Equal("Call team", payload.Title);
        Assert.Contains("09:30", payload.Body, StringComparison.Ordinal);
        Assert.Equal(
            [
                "action=complete&occurrenceId=" + reminder.Occurrence.Id.ToString("D"),
                "action=snooze10&occurrenceId=" + reminder.Occurrence.Id.ToString("D"),
                "action=ignore&occurrenceId=" + reminder.Occurrence.Id.ToString("D")
            ],
            payload.Buttons.Select(button => button.Arguments));
    }

    [Fact]
    public async Task Missed_summary_contains_count_and_at_most_three_titles()
    {
        var platform = new RecordingNotificationPlatform();
        var sink = new AppNotificationSink(platform, new RecordingImportantAlerts(), new RecordingActions());
        var reminders = new[]
        {
            TestData.Scheduled("A", "2026-08-01T09:00:00+08:00"),
            TestData.Scheduled("B", "2026-08-01T09:01:00+08:00"),
            TestData.Scheduled("C", "2026-08-01T09:02:00+08:00"),
            TestData.Scheduled("D", "2026-08-01T09:03:00+08:00")
        };

        await sink.DeliverMissedSummaryAsync(reminders, CancellationToken.None);

        var payload = Assert.Single(platform.Payloads);
        Assert.Contains("4", payload.Title, StringComparison.Ordinal);
        Assert.Contains("A", payload.Body, StringComparison.Ordinal);
        Assert.Contains("B", payload.Body, StringComparison.Ordinal);
        Assert.Contains("C", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("D", payload.Body, StringComparison.Ordinal);
        Assert.Equal("section=missed", payload.ActivationArguments);
    }

    [Fact]
    public async Task Valid_button_activation_calls_only_the_matching_action()
    {
        var id = Guid.Parse("4b3eb3c9-970d-47d7-89e2-bab9778a406d");
        var actions = new RecordingActions();
        var sink = new AppNotificationSink(new RecordingNotificationPlatform(), new RecordingImportantAlerts(), actions);

        var handled = await sink.HandleActivationAsync("action=snooze10&occurrenceId=" + id, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(["snooze10:" + id], actions.Calls);
    }

    private sealed class RecordingNotificationPlatform : INotificationPlatform
    {
        public NotificationHealth Health => NotificationHealth.Available;
        public List<NotificationPayload> Payloads { get; } = [];
        public Task ShowAsync(NotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
        public Task OpenSettingsAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingImportantAlerts : IImportantAlertDelivery
    {
        public Task EnqueueAsync(ReminderAlert alert, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingActions : IReminderActionService
    {
        public List<string> Calls { get; } = [];
        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) { Calls.Add("complete:" + occurrenceId); return Task.CompletedTask; }
        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) { Calls.Add("ignore:" + occurrenceId); return Task.CompletedTask; }
        public Task<ReminderOccurrence> SnoozeAsync(Guid occurrenceId, TimeSpan delay, CancellationToken ct)
        {
            Calls.Add("snooze" + delay.TotalMinutes + ":" + occurrenceId);
            return Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), DateTimeOffset.UtcNow));
        }
    }
}
