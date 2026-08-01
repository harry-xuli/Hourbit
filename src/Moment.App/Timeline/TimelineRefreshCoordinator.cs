using System.Windows.Threading;

namespace Moment.App.Timeline;

public sealed class TimelineRefreshCoordinator
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<Task> _reload;
    private readonly object _gate = new();
    private TaskCompletionSource? _drain;
    private bool _active;
    private bool _pending;

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
    }

    public Task RequestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task request;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            if (_active)
            {
                _pending = true;
            }
            else
            {
                _active = true;
                start = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _drain = start;
            }

            request = _drain!.Task;
        }

        if (start is not null)
            _ = ReloadUntilDrainedAsync(start);

        return ct.CanBeCanceled ? request.WaitAsync(ct) : request;
    }

    private async Task ReloadUntilDrainedAsync(TaskCompletionSource drain)
    {
        try
        {
            while (true)
            {
                await _dispatcher.InvokeAsync(_reload).Task.Unwrap();
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
