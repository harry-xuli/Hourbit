using Moment.Core.Abstractions;
using Moment.Core.Domain;

namespace Moment.Core.Scheduling;

public sealed record SchedulerDeliveryFailure(ScheduledReminder Reminder, Exception Exception);

public sealed class ReminderScheduler : ISchedulerSignal, IDisposable
{
    private readonly IReminderRepository _repository;
    private readonly IReminderSink _sink;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _loop;
    private bool _disposed;

    public event Action<SchedulerDeliveryFailure>? DeliveryFailed;

    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _loop ?? Task.CompletedTask;
            }
        }
    }

    public ReminderScheduler(IReminderRepository repository, IReminderSink sink, IClock clock)
    {
        _repository = repository;
        _sink = sink;
        _clock = clock;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task? completedLoop = null;
            CancellationTokenSource? completedCancellation = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_loop is { IsCompleted: false })
                    return;
                if (_loop is not null)
                {
                    completedLoop = _loop;
                    completedCancellation = _runCancellation;
                    _loop = null;
                    _runCancellation = null;
                }
            }

            if (completedLoop is not null)
            {
                try
                {
                    await completedLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    completedCancellation?.IsCancellationRequested == true)
                {
                }
                finally
                {
                    completedCancellation?.Dispose();
                }
            }

            ct.ThrowIfCancellationRequested();
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellation.Token, ct);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _runCancellation = runCancellation;
                _loop = Task.Run(
                    () => RunAsync(runCancellation.Token),
                    CancellationToken.None);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task? loop;
            CancellationTokenSource? runCancellation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                loop = _loop;
                runCancellation = _runCancellation;
            }

            if (loop is null)
                return;

            runCancellation!.Cancel();
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                runCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loop, loop))
                    {
                        _loop = null;
                        _runCancellation = null;
                    }
                }
                runCancellation.Dispose();
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _refreshSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // The queued signal already guarantees a re-query.
            }
        }
    }

    public void Dispose()
    {
        _transitionGate.Wait();
        try
        {
            Task? loop;
            CancellationTokenSource? runCancellation;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                loop = _loop;
                runCancellation = _runCancellation;
            }

            _disposeCancellation.Cancel();
            runCancellation?.Cancel();
            try
            {
                loop?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            runCancellation?.Dispose();
            _disposeCancellation.Dispose();
            _refreshSignal.Dispose();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var scheduled = await _repository.GetScheduledAsync(ct).ConfigureAwait(false);
                var next = scheduled
                    .OrderBy(reminder => reminder.Occurrence.DueAt)
                    .ThenBy(reminder => reminder.Occurrence.Id)
                    .FirstOrDefault();
                if (next is null)
                {
                    await _refreshSignal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = _clock.DelayUntilAsync(next.Occurrence.DueAt, waitCancellation.Token);
                var refresh = _refreshSignal.WaitAsync(waitCancellation.Token);
                var completed = await Task.WhenAny(delay, refresh).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                waitCancellation.Cancel();

                if (_clock.Now < next.Occurrence.DueAt)
                {
                    continue;
                }

                var due = await _repository.GetDueAsync(_clock.Now, ct).ConfigureAwait(false);
                foreach (var reminder in due.OrderBy(reminder => reminder.Occurrence.DueAt).ThenBy(reminder => reminder.Occurrence.Id))
                {
                    if (await _repository.TryMarkFiredAsync(reminder.Occurrence.Id, _clock.Now, ct).ConfigureAwait(false))
                    {
                        try
                        {
                            await _sink.DeliverAsync(reminder, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            ReportDeliveryFailure(new SchedulerDeliveryFailure(reminder, exception));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void ReportDeliveryFailure(SchedulerDeliveryFailure failure)
    {
        var handlers = DeliveryFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<SchedulerDeliveryFailure> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(failure);
            }
            catch
            {
                // Observers must not be allowed to stop the scheduling loop.
            }
        }
    }
}
