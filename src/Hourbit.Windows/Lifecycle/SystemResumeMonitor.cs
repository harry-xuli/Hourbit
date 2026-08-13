using Hourbit.Windows.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hourbit.Windows.Lifecycle;

public enum ResumeReason
{
    Unlock,
    PowerResume,
    TimeChanged,
    TimeZoneChanged
}

public interface ISystemResumeMonitor : IAsyncDisposable
{
    void Start();
}

public interface ISystemResumeEventSource : IDisposable
{
    event EventHandler<ResumeReason>? Resumed;
    void Start();
    void Stop();
}

public interface IResumeDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public enum LifecycleWindowMode
{
    MessageOnly,
    HiddenTopLevel
}

public enum NativeLifecycleReason
{
    Unlock,
    PowerResume,
    TimeChanged,
    TimeZoneChanged
}

public interface ILifecycleNativeWindow : IDisposable
{
    event EventHandler<NativeLifecycleReason>? Signaled;
    void Start();
    void Stop();
}

public interface ILifecycleNativeWindowFactory
{
    ILifecycleNativeWindow Create(LifecycleWindowMode mode);
}

public sealed class SystemResumeMonitor : ISystemResumeMonitor
{
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);
    private static readonly AsyncLocal<RecoveryScope?> CurrentRecovery = new();

    private readonly ISystemResumeEventSource _source;
    private readonly Func<ResumeReason, CancellationToken, Task> _recover;
    private readonly IResumeDelay _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<Task> _debounces = [];
    private readonly List<Task> _recoveries = [];
    private CancellationTokenSource? _debounce;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;

    public event Action<Exception>? RecoveryFailed;

    public SystemResumeMonitor(
        Func<ResumeReason, CancellationToken, Task> recover,
        TimeProvider? timeProvider = null)
        : this(new WindowsSystemResumeEventSource(), recover, new TimeProviderResumeDelay(timeProvider))
    {
    }

    public SystemResumeMonitor(
        ISystemResumeEventSource source,
        Func<ResumeReason, CancellationToken, Task> recover,
        IResumeDelay? delay = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _delay = delay ?? new TimeProviderResumeDelay();
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _source.Resumed += OnResumed;
            _source.Start();
            _started = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                if (_started)
                {
                    _source.Resumed -= OnResumed;
                    _source.Stop();
                    _started = false;
                }
                _lifetime.Cancel();
                _debounce?.Cancel();
                _disposeTask = CompleteDisposalAsync(_debounces.ToArray());
            }
            disposeTask = _disposeTask;
        }
        return CurrentRecovery.Value is { Active: true } scope &&
            ReferenceEquals(scope.Owner, this)
            ? ValueTask.CompletedTask
            : new ValueTask(disposeTask);
    }

    private void OnResumed(object? sender, ResumeReason reason)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed || !_started)
                return;
            previous = _debounce;
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounces.Add(DebounceAsync(reason, _debounce));
        }
        previous?.Cancel();
        previous?.Dispose();
    }

    private async Task DebounceAsync(ResumeReason reason, CancellationTokenSource debounce)
    {
        try
        {
            await _delay.DelayAsync(DebounceWindow, debounce.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_debounce, debounce))
                    return;
            }
            QueueRecovery(reason);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
    }

    private void QueueRecovery(ResumeReason reason)
    {
        lock (_gate)
        {
            if (!_disposed)
                _recoveries.Add(RecoverTrackedAsync(reason));
        }
    }

    private async Task RecoverTrackedAsync(ResumeReason reason)
    {
        await Task.Yield();
        var previous = CurrentRecovery.Value;
        var scope = new RecoveryScope(this);
        CurrentRecovery.Value = scope;
        try
        {
            await _recover(reason, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                RecoveryFailed?.Invoke(exception);
            }
            catch
            {
                // The recovery exception is observed even if a diagnostic observer fails.
            }
        }
        finally
        {
            scope.Active = false;
            CurrentRecovery.Value = previous;
        }
    }

    private async Task CompleteDisposalAsync(Task[] debounces)
    {
        await Task.WhenAll(debounces).ConfigureAwait(false);
        Task[] recoveries;
        lock (_gate)
            recoveries = _recoveries.ToArray();
        await Task.WhenAll(recoveries).ConfigureAwait(false);
        _source.Dispose();
        _debounce?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class RecoveryScope(SystemResumeMonitor owner)
    {
        public SystemResumeMonitor Owner { get; } = owner;
        public bool Active { get; set; } = true;
    }
}

public sealed class TimeProviderResumeDelay : IResumeDelay
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderResumeDelay(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}

/// <summary>Windows event adapter; tests inject <see cref="ISystemResumeEventSource"/>.</summary>
public sealed class WindowsSystemResumeEventSource : ISystemResumeEventSource
{
    private readonly ILifecycleNativeWindow _window;
    private bool _started;
    private bool _disposed;

    public WindowsSystemResumeEventSource(ILifecycleNativeWindowFactory? factory = null)
    {
        factory ??= new LifecycleNativeWindowFactory();
        _window = factory.Create(LifecycleWindowMode.HiddenTopLevel);
    }

    public event EventHandler<ResumeReason>? Resumed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _window.Signaled += OnSignaled;
        _window.Start();
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;
        _window.Stop();
        _window.Signaled -= OnSignaled;
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
        _window.Dispose();
    }

    private void OnSignaled(object? sender, NativeLifecycleReason reason) =>
        Resumed?.Invoke(this, reason switch
        {
            NativeLifecycleReason.Unlock => ResumeReason.Unlock,
            NativeLifecycleReason.PowerResume => ResumeReason.PowerResume,
            NativeLifecycleReason.TimeChanged => ResumeReason.TimeChanged,
            NativeLifecycleReason.TimeZoneChanged => ResumeReason.TimeZoneChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        });

    private sealed class LifecycleNativeWindowFactory : ILifecycleNativeWindowFactory
    {
        public ILifecycleNativeWindow Create(LifecycleWindowMode mode)
        {
            if (mode != LifecycleWindowMode.HiddenTopLevel)
                throw new ArgumentOutOfRangeException(nameof(mode));
            return new PowerBroadcastWindow();
        }
    }

    private sealed class PowerBroadcastWindow : ILifecycleNativeWindow
    {
        private const int WmPowerBroadcast = 0x0218;
        private const int WmTimeChange = 0x001E;
        private const int WmSettingChange = 0x001A;
        private const int WmSessionChange = 0x02B1;
        private const int SessionUnlock = 0x0008;
        private const uint NotifyThisSession = 0;
        private const int ResumeAutomatic = 0x0012;
        private const int ResumeSuspend = 0x0007;
        private readonly Win32MessageOnlyWindow _window =
            new("Hourbit lifecycle events", Win32WindowMode.HiddenTopLevel);
        private string _timeZoneId = TimeZoneInfo.Local.Id;
        private bool _started;
        private bool _disposed;

        public PowerBroadcastWindow() => _window.MessageReceived += OnMessageReceived;

        public event EventHandler<NativeLifecycleReason>? Signaled;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            if (!NativeMethods.WTSRegisterSessionNotification(_window.Handle, NotifyThisSession))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _started = true;
        }

        public void Stop()
        {
            if (!_started)
                return;
            NativeMethods.WTSUnRegisterSessionNotification(_window.Handle);
            _started = false;
        }

        private void OnMessageReceived(int message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmSessionChange && wParam.ToInt32() == SessionUnlock)
                Signaled?.Invoke(this, NativeLifecycleReason.Unlock);
            else if (message == WmPowerBroadcast &&
                wParam.ToInt32() is ResumeAutomatic or ResumeSuspend)
                Signaled?.Invoke(this, NativeLifecycleReason.PowerResume);
            else if (message is WmTimeChange or WmSettingChange)
            {
                TimeZoneInfo.ClearCachedData();
                var currentTimeZoneId = TimeZoneInfo.Local.Id;
                var timeZoneChanged =
                    !string.Equals(currentTimeZoneId, _timeZoneId, StringComparison.Ordinal);
                _timeZoneId = currentTimeZoneId;
                if (timeZoneChanged)
                    Signaled?.Invoke(this, NativeLifecycleReason.TimeZoneChanged);
                else if (message == WmTimeChange)
                    Signaled?.Invoke(this, NativeLifecycleReason.TimeChanged);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Stop();
            _disposed = true;
            _window.MessageReceived -= OnMessageReceived;
            _window.Dispose();
        }

        private static class NativeMethods
        {
            [DllImport("wtsapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

            [DllImport("wtsapi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
        }
    }
}
