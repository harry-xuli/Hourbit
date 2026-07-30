using System.Threading.Channels;
using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Windows.Notifications;

namespace Moment.Windows.Alerts;

public sealed record ImportantAlertFailure(ReminderAlert Alert, Exception Exception);

public sealed class ImportantAlertController : IImportantAlertDelivery, IAsyncDisposable
{
    private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(25);
    private readonly Channel<PendingAlert> _queue = Channel.CreateUnbounded<PendingAlert>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly IImportantAlertPresenter _presenter;
    private readonly IReminderActionService _actions;
    private readonly IImportantAlertAudio _audio;
    private readonly TimeProvider _timeProvider;
    private readonly Task _worker;

    public event Action<ImportantAlertFailure>? PresentationFailed;

    public ImportantAlertController(
        IImportantAlertPresenter presenter,
        IReminderActionService actions,
        IImportantAlertAudio? audio = null,
        TimeProvider? timeProvider = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _audio = audio ?? SilentImportantAlertAudio.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public Task EnqueueAsync(ReminderAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ct.ThrowIfCancellationRequested();
        var pending = new PendingAlert(alert);
        if (!_queue.Writer.TryWrite(pending))
        {
            throw new InvalidOperationException("Important alert delivery has stopped.");
        }

        return pending.Completion.Task.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var first in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var batch = new List<PendingAlert> { first };
            await Task.Delay(CoalescingWindow, _timeProvider).ConfigureAwait(false);
            while (_queue.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
            }

            foreach (var pending in batch.OrderBy(item => item.Alert.DueAt).ThenBy(item => item.Alert.OccurrenceId))
            {
                await PresentAsync(pending).ConfigureAwait(false);
            }
        }
    }

    private async Task PresentAsync(PendingAlert pending)
    {
        try
        {
            await StartAudioAsync(pending.Alert).ConfigureAwait(false);
            ImportantAlertAction action;
            try
            {
                action = await _presenter.ShowAsync(pending.Alert, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportPresentationFailure(new ImportantAlertFailure(pending.Alert, exception));
                pending.Completion.TrySetResult();
                return;
            }

            await ApplyActionAsync(pending.Alert.OccurrenceId, action).ConfigureAwait(false);
            pending.Completion.TrySetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            pending.Completion.TrySetException(exception);
        }
        finally
        {
            try
            {
                await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private async Task StartAudioAsync(ReminderAlert alert)
    {
        if (!string.IsNullOrWhiteSpace(alert.CustomAudioPath))
        {
            try
            {
                await _audio.StartCustomLoopAsync(alert.CustomAudioPath, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The application-side implementation provides the embedded default-alert.wav loop.
            }
        }

        await _audio.StartDefaultLoopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Task ApplyActionAsync(Guid occurrenceId, ImportantAlertAction action) => action switch
    {
        ImportantAlertAction.Complete => _actions.CompleteAsync(occurrenceId, CancellationToken.None),
        ImportantAlertAction.Ignore => _actions.IgnoreAsync(occurrenceId, CancellationToken.None),
        ImportantAlertAction.Snooze5 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(5), CancellationToken.None),
        ImportantAlertAction.Snooze10 or ImportantAlertAction.Close => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(10), CancellationToken.None),
        ImportantAlertAction.Snooze30 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(30), CancellationToken.None),
        ImportantAlertAction.Snooze60 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(60), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private void ReportPresentationFailure(ImportantAlertFailure failure)
    {
        foreach (Action<ImportantAlertFailure> observer in PresentationFailed?.GetInvocationList() ?? [])
        {
            try
            {
                observer(failure);
            }
            catch
            {
                // Fault observers cannot stop later important alerts.
            }
        }
    }

    private sealed class PendingAlert(ReminderAlert alert)
    {
        public ReminderAlert Alert { get; } = alert;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SilentImportantAlertAudio : IImportantAlertAudio
    {
        public static SilentImportantAlertAudio Instance { get; } = new();
        public Task StartCustomLoopAsync(string audioPath, CancellationToken ct) => Task.CompletedTask;
        public Task StartDefaultLoopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
