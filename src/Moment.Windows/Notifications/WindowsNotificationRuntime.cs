using Moment.Core.Services;

namespace Moment.Windows.Notifications;

/// <summary>Application-lifecycle hook for Task 8 composition.</summary>
public sealed class WindowsNotificationRuntime : IAsyncDisposable
{
    private readonly NotificationActivationRouter _router;
    private int _started;
    private int _disposed;

    public WindowsNotificationRuntime(INotificationActivationSource source, IReminderActionService actions, INotificationNavigator navigator) =>
        _router = new NotificationActivationRouter(source, actions, navigator);

    public WindowsNotificationRuntime(IReminderActionService actions, INotificationNavigator navigator) :
        this(new WindowsAppNotificationActivationSource(), actions, navigator) { }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0) _router.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Exchange(ref _started, 0) != 0)
            await _router.DisposeAsync();
    }
}
