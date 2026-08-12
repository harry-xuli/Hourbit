using Moment.Core.Services;

namespace Moment.Windows.Notifications;

/// <summary>Application-lifecycle hook for Task 8 composition.</summary>
public sealed class WindowsNotificationRuntime : IAsyncDisposable
{
    private readonly NotificationActivationRouter _router;
    private readonly object _gate = new();
    private bool _started;
    private TaskCompletionSource? _disposeCompletion;

    public WindowsNotificationRuntime(
        INotificationActivationSource source,
        IReminderActionService actions,
        INotificationNavigator navigator,
        IReminderActionCompletedObserver? actionCompletedObserver = null) =>
        _router = new NotificationActivationRouter(
            source, actions, navigator, actionCompletedObserver);

    public WindowsNotificationRuntime(
        IReminderActionService actions,
        INotificationNavigator navigator,
        IReminderActionCompletedObserver? actionCompletedObserver = null) :
        this(new WindowsAppNotificationActivationSource(), actions, navigator,
            actionCompletedObserver) { }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeCompletion is not null, this);
            if (_started) return;
            _router.Start();
            _started = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        var disposeRouter = false;
        lock (_gate)
        {
            if (_disposeCompletion is not null)
                return new ValueTask(_disposeCompletion.Task);

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
            disposeRouter = _started;
            _started = false;
            if (disposeRouter)
                _router.StopAccepting();
        }

        if (disposeRouter)
            _ = CompleteDisposalAsync(completion);
        else
            completion.SetResult();

        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await _router.DisposeAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}
