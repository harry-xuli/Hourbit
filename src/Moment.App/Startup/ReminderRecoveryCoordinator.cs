using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Scheduling;

namespace Moment.App.Startup;

public sealed class ReminderRecoveryCoordinator : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _stopScheduler;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _recover;
    private readonly Func<CancellationToken, Task> _startScheduler;
    private readonly Func<CancellationToken, Task> _refreshTimeline;
    private readonly IClock _clock;
    private readonly CancellationToken _appLifetime;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;
    private int _admitted;
    private bool _disposed;

    public ReminderRecoveryCoordinator(
        ReminderScheduler scheduler,
        ReminderRecoveryService recovery,
        IClock clock,
        TimelineRefreshCoordinator timelineRefresh,
        CancellationToken appLifetime)
        : this(
            scheduler is null
                ? throw new ArgumentNullException(nameof(scheduler))
                : new Func<CancellationToken, Task>(scheduler.StopAsync),
            recovery is null
                ? throw new ArgumentNullException(nameof(recovery))
                : async (now, ct) => _ = await recovery.RecoverAsync(now, ct),
            new Func<CancellationToken, Task>(scheduler.StartAsync),
            timelineRefresh is null
                ? throw new ArgumentNullException(nameof(timelineRefresh))
                : new Func<CancellationToken, Task>(timelineRefresh.RequestAndDrainAsync),
            clock,
            appLifetime)
    {
    }

    internal ReminderRecoveryCoordinator(
        Func<CancellationToken, Task> stopScheduler,
        Func<DateTimeOffset, CancellationToken, Task> recover,
        Func<CancellationToken, Task> startScheduler,
        Func<CancellationToken, Task> refreshTimeline,
        IClock clock,
        CancellationToken appLifetime)
    {
        _stopScheduler = stopScheduler ?? throw new ArgumentNullException(nameof(stopScheduler));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _startScheduler = startScheduler ?? throw new ArgumentNullException(nameof(startScheduler));
        _refreshTimeline = refreshTimeline ?? throw new ArgumentNullException(nameof(refreshTimeline));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _appLifetime = appLifetime;
    }

    public Task RecoverAndRefreshAsync(CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _admitted++;
        }

        return RecoverAdmittedAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            if (_admitted == 0)
            {
                _recoveryGate.Dispose();
                _disposeTask = Task.CompletedTask;
            }
            else
            {
                _drained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = DisposeAfterDrainAsync(_drained.Task);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task RecoverAdmittedAsync(CancellationToken ct)
    {
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _appLifetime);
            await _recoveryGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                try
                {
                    await _stopScheduler(operation.Token).ConfigureAwait(false);
                    await _recover(_clock.Now, operation.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (!_appLifetime.IsCancellationRequested)
                        await _startScheduler(_appLifetime).ConfigureAwait(false);
                }

                operation.Token.ThrowIfCancellationRequested();
                await _refreshTimeline(operation.Token).ConfigureAwait(false);
            }
            finally
            {
                _recoveryGate.Release();
            }
        }
        finally
        {
            ReleaseAdmission();
        }
    }

    private void ReleaseAdmission()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleGate)
        {
            _admitted--;
            if (_disposed && _admitted == 0)
                drained = _drained;
        }
        drained?.TrySetResult();
    }

    private async Task DisposeAfterDrainAsync(Task drain)
    {
        await drain.ConfigureAwait(false);
        _recoveryGate.Dispose();
    }
}
