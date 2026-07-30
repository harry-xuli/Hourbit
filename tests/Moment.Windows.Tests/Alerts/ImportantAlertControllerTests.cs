using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.TestSupport;
using Moment.Windows.Alerts;

namespace Moment.Windows.Tests.Alerts;

public sealed class ImportantAlertControllerTests
{
    [Fact]
    public async Task Important_alerts_are_presented_one_at_a_time_in_due_order()
    {
        var presenter = new RecordingPresenter(ImportantAlertAction.Ignore);
        var actions = new RecordingActions();
        await using var controller = new ImportantAlertController(presenter, actions);

        await Task.WhenAll(
            controller.EnqueueAsync(TestData.Alert("B", dueMinute: 2), CancellationToken.None),
            controller.EnqueueAsync(TestData.Alert("A", dueMinute: 1), CancellationToken.None));

        Assert.Equal(["A", "B"], presenter.Titles);
        Assert.Equal(1, presenter.MaximumConcurrency);
    }

    [Fact]
    public async Task Presenter_action_is_mapped_to_reminder_action_service()
    {
        var alert = TestData.Alert("Important", dueMinute: 1);
        var actions = new RecordingActions();
        await using var controller = new ImportantAlertController(new RecordingPresenter(ImportantAlertAction.Snooze30), actions);

        await controller.EnqueueAsync(alert, CancellationToken.None);

        Assert.Equal(["snooze30:" + alert.OccurrenceId], actions.Calls);
    }

    [Fact]
    public async Task Failed_custom_audio_falls_back_to_embedded_default_audio()
    {
        var audio = new RecordingAudio(failCustom: true);
        await using var controller = new ImportantAlertController(
            new RecordingPresenter(ImportantAlertAction.Ignore), new RecordingActions(), audio);
        var alert = TestData.Alert("Important", dueMinute: 1) with { CustomAudioPath = "C:\\missing.wav" };

        await controller.EnqueueAsync(alert, CancellationToken.None);

        Assert.Equal(["custom:C:\\missing.wav", "default", "stop"], audio.Calls);
    }

    [Fact]
    public async Task Presenter_failure_is_observed_without_changing_the_fired_occurrence()
    {
        var alert = TestData.Alert("Important", dueMinute: 1);
        var actions = new RecordingActions();
        await using var controller = new ImportantAlertController(new ThrowingPresenter(), actions);
        ImportantAlertFailure? reported = null;
        controller.PresentationFailed += failure => reported = failure;

        await controller.EnqueueAsync(alert, CancellationToken.None);

        Assert.NotNull(reported);
        Assert.Equal(alert, reported.Alert);
        Assert.Empty(actions.Calls);
    }

    private sealed class RecordingPresenter(ImportantAlertAction action) : IImportantAlertPresenter
    {
        private int _active;
        public List<string> Titles { get; } = [];
        public int MaximumConcurrency { get; private set; }
        public async Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            Titles.Add(alert.Title);
            await Task.Yield();
            Interlocked.Decrement(ref _active);
            return action;
        }
    }

    private sealed class ThrowingPresenter : IImportantAlertPresenter
    {
        public Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct) =>
            Task.FromException<ImportantAlertAction>(new InvalidOperationException("window failed"));
    }

    private sealed class RecordingAudio(bool failCustom) : IImportantAlertAudio
    {
        public List<string> Calls { get; } = [];
        public Task StartCustomLoopAsync(string audioPath, CancellationToken ct)
        {
            Calls.Add("custom:" + audioPath);
            return failCustom ? Task.FromException(new InvalidOperationException("missing")) : Task.CompletedTask;
        }
        public Task StartDefaultLoopAsync(CancellationToken ct) { Calls.Add("default"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Calls.Add("stop"); return Task.CompletedTask; }
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
