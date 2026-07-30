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

    [Fact]
    public async Task Bounded_queue_applies_backpressure_without_losing_accepted_alerts()
    {
        var presenter = new BlockingPresenter();
        var actions = new RecordingActions();
        await using var controller = new ImportantAlertController(presenter, actions, queueCapacity: 1);
        var first = controller.EnqueueAsync(TestData.Alert("A", dueMinute: 1), CancellationToken.None);
        await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = controller.EnqueueAsync(TestData.Alert("B", dueMinute: 2), CancellationToken.None);
        using var waitingCancellation = new CancellationTokenSource();
        var third = controller.EnqueueAsync(TestData.Alert("C", dueMinute: 3), waitingCancellation.Token);

        await Task.Delay(50);
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        presenter.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(["A", "B"], presenter.Titles);
        Assert.Equal(["ignore:" + presenter.OccurrenceIds[0], "ignore:" + presenter.OccurrenceIds[1]], actions.Calls);
    }

    [Fact]
    public async Task Disposal_cancels_in_flight_and_queued_alerts_without_presenting_later_alerts()
    {
        var presenter = new BlockingPresenter();
        var actions = new RecordingActions();
        var controller = new ImportantAlertController(presenter, actions, queueCapacity: 1);
        var first = controller.EnqueueAsync(TestData.Alert("A", dueMinute: 1), CancellationToken.None);
        await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = controller.EnqueueAsync(TestData.Alert("B", dueMinute: 2), CancellationToken.None);

        await controller.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(["A"], presenter.Titles);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task Accepted_alert_completes_even_if_its_caller_token_is_cancelled_after_admission()
    {
        var presenter = new BlockingPresenter();
        var actions = new RecordingActions();
        await using var controller = new ImportantAlertController(presenter, actions, queueCapacity: 1);
        using var callerCancellation = new CancellationTokenSource();
        var alert = TestData.Alert("A", dueMinute: 1);
        var completion = controller.EnqueueAsync(alert, callerCancellation.Token);
        await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        callerCancellation.Cancel();
        presenter.Release();
        await completion;

        Assert.Equal(["ignore:" + alert.OccurrenceId], actions.Calls);
    }

    [Fact]
    public async Task Accepted_alert_does_not_complete_until_audio_cleanup_succeeds()
    {
        var audio = new BlockingStopAudio();
        await using var controller = new ImportantAlertController(
            new RecordingPresenter(ImportantAlertAction.Ignore),
            new RecordingActions(), audio);

        var completion = controller.EnqueueAsync(
            TestData.Alert("A", dueMinute: 1), CancellationToken.None);
        await audio.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(completion.IsCompleted);
        audio.ReleaseStop();
        await completion;
    }

    [Fact]
    public async Task Audio_cleanup_failure_is_reported_to_the_accepted_alert_caller()
    {
        var audio = new BlockingStopAudio(failStop: true);
        await using var controller = new ImportantAlertController(
            new RecordingPresenter(ImportantAlertAction.Ignore),
            new RecordingActions(), audio);

        var completion = controller.EnqueueAsync(
            TestData.Alert("A", dueMinute: 1), CancellationToken.None);
        await audio.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        audio.ReleaseStop();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => completion);
        Assert.Equal("audio cleanup failed", error.Message);
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

    private sealed class BlockingPresenter : IImportantAlertPresenter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Titles { get; } = [];
        public List<Guid> OccurrenceIds { get; } = [];

        public async Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct)
        {
            Titles.Add(alert.Title);
            OccurrenceIds.Add(alert.OccurrenceId);
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return ImportantAlertAction.Ignore;
        }

        public void Release() => _release.TrySetResult();
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

    private sealed class BlockingStopAudio(bool failStop = false) : IImportantAlertAudio
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartCustomLoopAsync(string audioPath, CancellationToken ct) =>
            Task.CompletedTask;
        public Task StartDefaultLoopAsync(CancellationToken ct) =>
            Task.CompletedTask;
        public async Task StopAsync(CancellationToken ct)
        {
            StopStarted.TrySetResult();
            await _release.Task;
            if (failStop)
                throw new InvalidOperationException("audio cleanup failed");
        }
        public void ReleaseStop() => _release.TrySetResult();
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
