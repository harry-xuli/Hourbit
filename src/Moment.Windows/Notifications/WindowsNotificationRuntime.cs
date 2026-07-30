using Moment.Core.Services;

namespace Moment.Windows.Notifications;

/// <summary>Application-lifecycle hook for Task 8 composition.</summary>
public sealed class WindowsNotificationRuntime : IAsyncDisposable
{
    private readonly NotificationActivationRouter _router;
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;

    public WindowsNotificationRuntime(INotificationActivationSource source, IReminderActionService actions, INotificationNavigator navigator) =>
        _router = new NotificationActivationRouter(source, actions, navigator);

    public WindowsNotificationRuntime(IReminderActionService actions, INotificationNavigator navigator) :
        this(new WindowsAppNotificationActivationSource(), actions, navigator) { }

    public void Start()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (_started) return; _router.Start(); _started = true; }
    }

    public async ValueTask DisposeAsync()
    {
        var disposeRouter = false;
        lock (_gate) { if (_disposed) return; _disposed = true; disposeRouter = _started; _started = false; }
        if (disposeRouter) await _router.DisposeAsync();
    }
}
