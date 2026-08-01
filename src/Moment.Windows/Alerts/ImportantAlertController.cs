using System.Threading.Channels;
using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Windows.Notifications;

namespace Moment.Windows.Alerts;

public sealed record ImportantAlertFailure(ReminderAlert Alert, Exception Exception);

public sealed class ImportantAlertController : IImportantAlertDelivery, IAsyncDisposable
{
    /// <summary>Maximum alerts waiting behind the one visible important-alert window.</summary>
    public const int DefaultQueueCapacity = 32;

    private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(25);
    private readonly Channel<PendingAlert> _queue;
    private readonly IImportantAlertPresenter _presenter;
    private readonly IReminderActionService _actions;
    private readonly IImportantAlertAudio _audio;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _disposed;

    public event Action<ImportantAlertFailure>? PresentationFailed;

    public ImportantAlertController(
        IImportantAlertPresenter presenter,
        IReminderActionService actions,
        IImportantAlertAudio? audio = null,
        TimeProvider? timeProvider = null,
        int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _audio = audio ?? SilentImportantAlertAudio.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = Channel.CreateBounded<PendingAlert>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public async Task EnqueueAsync(ReminderAlert alert, CancellationToken ct)
    {
        var pending = new PendingAlert(alert, trackCompletion: true);
        await AdmitAsync(pending, ct).ConfigureAwait(false);
        await pending.Completion!.Task.ConfigureAwait(false);
    }

    public Task AdmitAsync(ReminderAlert alert, CancellationToken ct) =>
        AdmitAsync(new PendingAlert(alert, trackCompletion: false), ct);

    private async Task AdmitAsync(PendingAlert pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending.Alert);
        ct.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();

        try
        {
            while (await _queue.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_queue.Writer.TryWrite(pending))
                {
                    // Ownership transfers at admission; later caller cancellation cannot drop it.
                    return;
                }
            }
        }
        catch (ChannelClosedException) when (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException(_lifetime.Token);
        }

        throw new InvalidOperationException("Important alert delivery has stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _worker.ConfigureAwait(false);
            return;
        }

        _lifetime.Cancel();
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (!_queue.Reader.TryRead(out var first))
                {
                    continue;
                }

                var batch = new List<PendingAlert> { first };
                try
                {
                    await Task.Delay(CoalescingWindow, _timeProvider, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    Cancel(batch);
                    return;
                }

                while (_queue.Reader.TryRead(out var pending))
                {
                    batch.Add(pending);
                }

                var ordered = batch.OrderBy(item => item.Alert.DueAt).ThenBy(item => item.Alert.OccurrenceId).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    if (_lifetime.IsCancellationRequested)
                    {
                        Cancel(ordered[index..]);
                        return;
                    }

                    await PresentAsync(ordered[index], _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Disposal completes accepted requests in the finally block below.
        }
        finally
        {
            while (_queue.Reader.TryRead(out var pending))
            {
                pending.Completion?.TrySetCanceled(_lifetime.Token);
            }
        }
    }

    private async Task PresentAsync(PendingAlert pending, CancellationToken ct)
    {
        Exception? failure = null;
        var cancelled = false;
        try
        {
            await StartAudioAsync(pending.Alert, ct).ConfigureAwait(false);
            ImportantAlertAction? action = null;
            try
            {
                action = await _presenter.ShowAsync(pending.Alert, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportPresentationFailure(new ImportantAlertFailure(pending.Alert, exception));
            }
            if (action is { } selectedAction)
                await ApplyActionAsync(pending.Alert.OccurrenceId, selectedAction, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            // Teardown must not be skipped just because the controller lifetime was cancelled.
            await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        if (cancelled)
        {
            pending.Completion?.TrySetCanceled(ct);
            throw new OperationCanceledException(ct);
        }

        if (failure is not null)
        {
            ReportPresentationFailure(new ImportantAlertFailure(
                pending.Alert, failure));
            pending.Completion?.TrySetException(failure);
        }
        else
            pending.Completion?.TrySetResult();
    }

    private async Task StartAudioAsync(ReminderAlert alert, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(alert.CustomAudioPath))
        {
            try
            {
                await _audio.StartCustomLoopAsync(alert.CustomAudioPath, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The application-side implementation provides the embedded default-alert.wav loop.
            }
        }

        await _audio.StartDefaultLoopAsync(ct).ConfigureAwait(false);
    }

    private Task ApplyActionAsync(Guid occurrenceId, ImportantAlertAction action, CancellationToken ct) => action switch
    {
        ImportantAlertAction.Complete => _actions.CompleteAsync(occurrenceId, ct),
        ImportantAlertAction.Ignore => _actions.IgnoreAsync(occurrenceId, ct),
        ImportantAlertAction.Snooze5 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(5), ct),
        ImportantAlertAction.Snooze10 or ImportantAlertAction.Close => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(10), ct),
        ImportantAlertAction.Snooze30 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(30), ct),
        ImportantAlertAction.Snooze60 => _actions.SnoozeAsync(occurrenceId, TimeSpan.FromMinutes(60), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private void Cancel(IEnumerable<PendingAlert> pendingAlerts)
    {
        foreach (var pending in pendingAlerts)
        {
            pending.Completion?.TrySetCanceled(_lifetime.Token);
        }
    }

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

    private sealed class PendingAlert(ReminderAlert alert, bool trackCompletion)
    {
        public ReminderAlert Alert { get; } = alert;
        public TaskCompletionSource? Completion { get; } = trackCompletion
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
    }

    private sealed class SilentImportantAlertAudio : IImportantAlertAudio
    {
        public static SilentImportantAlertAudio Instance { get; } = new();
        public Task StartCustomLoopAsync(string audioPath, CancellationToken ct) => Task.CompletedTask;
        public Task StartDefaultLoopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
