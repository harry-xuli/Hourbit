using System.Windows.Threading;

namespace Moment.App.Timeline;

public sealed class TimelineRefreshCoordinator : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<Task> _reload;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _dispatcherShutdown = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _drain;
    private Task? _disposeTask;
    private bool _active;
    private bool _pending;
    private bool _disposed;

    public TimelineRefreshCoordinator(
        Dispatcher dispatcher,
        TimelineViewModel timeline)
        : this(dispatcher, CreateReload(timeline))
    {
    }

    internal TimelineRefreshCoordinator(Dispatcher dispatcher, Func<Task> reload)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
        _dispatcher.ShutdownStarted += OnDispatcherShutdown;
        if (_dispatcher.HasShutdownStarted)
            _dispatcherShutdown.TrySetResult();
    }

    public Task RequestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = Admit();
        return ct.CanBeCanceled ? request.WaitAsync(ct) : request;
    }

    internal Task RequestAndDrainAsync(CancellationToken ct)
    {
        return AwaitOwnedAsync(Admit(), ct);
    }

    internal Task DrainAsync(CancellationToken ct)
    {
        Task drain;
        lock (_gate)
            drain = _drain?.Task ?? Task.CompletedTask;
        return AwaitOwnedAsync(drain, ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = CompleteDisposalAsync(
                    _drain?.Task ?? Task.CompletedTask);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private Task Admit()
    {
        Task request;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active)
            {
                _pending = true;
            }
            else
            {
                _active = true;
                start = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ObserveFailure(start.Task);
                _drain = start;
            }

            request = _drain!.Task;
        }

        if (start is not null)
            _ = ReloadUntilDrainedAsync(start);

        return request;
    }

    private static async Task AwaitOwnedAsync(Task drain, CancellationToken ct)
    {
        try
        {
            await drain.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await drain.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task ReloadUntilDrainedAsync(TaskCompletionSource drain)
    {
        try
        {
            while (true)
            {
                await InvokeReloadAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_pending)
                    {
                        _pending = false;
                        continue;
                    }

                    _active = false;
                    _drain = null;
                }

                drain.TrySetResult();
                return;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _active = false;
                _pending = false;
                _drain = null;
            }

            drain.TrySetException(exception);
        }
    }

    private async Task InvokeReloadAsync()
    {
        var reload = _dispatcher.InvokeAsync(_reload).Task.Unwrap();
        var completed = await Task.WhenAny(reload, _dispatcherShutdown.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, reload) || reload.IsCompleted)
        {
            await reload.ConfigureAwait(false);
            return;
        }

        ObserveFailure(reload);
        throw new OperationCanceledException(
            "Timeline reload was abandoned because the UI dispatcher is shutting down.");
    }

    private async Task CompleteDisposalAsync(Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        finally
        {
            _dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        }
    }

    private void OnDispatcherShutdown(object? sender, EventArgs eventArgs) =>
        _dispatcherShutdown.TrySetResult();

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Func<Task> CreateReload(TimelineViewModel timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return async () =>
        {
            await timeline.LoadAsync();
            if (!string.IsNullOrWhiteSpace(timeline.ErrorMessage))
                throw new InvalidOperationException(timeline.ErrorMessage);
        };
    }
}
