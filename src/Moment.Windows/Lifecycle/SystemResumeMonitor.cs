using Moment.Windows.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Moment.Windows.Lifecycle;

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

public sealed class SystemResumeMonitor : ISystemResumeMonitor
{
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly ISystemResumeEventSource _source;
    private readonly Func<ResumeReason, CancellationToken, Task> _recover;
    private readonly IResumeDelay _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<Task> _pending = [];
    private CancellationTokenSource? _debounce;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;

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
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            if (_started)
            {
                _source.Resumed -= OnResumed;
                _source.Stop();
                _started = false;
            }
            _lifetime.Cancel();
            _debounce?.Cancel();
            return new ValueTask(_disposeTask = CompleteDisposalAsync(_pending.ToArray()));
        }
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
            _pending.Add(DebounceAsync(reason, _debounce));
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
            await _recover(reason, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteDisposalAsync(Task[] pending)
    {
        await Task.WhenAll(pending).ConfigureAwait(false);
        _source.Dispose();
        _debounce?.Dispose();
        _lifetime.Dispose();
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
    private readonly PowerBroadcastWindow _window = new();
    private bool _started;
    private bool _disposed;

    public event EventHandler<ResumeReason>? Resumed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _window.Unlocked += OnUnlocked;
        _window.PowerResumed += OnPowerResumed;
        _window.ClockChanged += OnClockChanged;
        _window.Start();
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;
        _window.Stop();
        _window.Unlocked -= OnUnlocked;
        _window.PowerResumed -= OnPowerResumed;
        _window.ClockChanged -= OnClockChanged;
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

    private void OnUnlocked(object? sender, EventArgs e) =>
        Resumed?.Invoke(this, ResumeReason.Unlock);

    private void OnPowerResumed(object? sender, EventArgs e) =>
        Resumed?.Invoke(this, ResumeReason.PowerResume);

    private void OnClockChanged(object? sender, ResumeReason reason) =>
        Resumed?.Invoke(this, reason);

    private sealed class PowerBroadcastWindow : IDisposable
    {
        private const int WmPowerBroadcast = 0x0218;
        private const int WmTimeChange = 0x001E;
        private const int WmSessionChange = 0x02B1;
        private const int SessionUnlock = 0x0008;
        private const uint NotifyThisSession = 0;
        private const int ResumeAutomatic = 0x0012;
        private const int ResumeSuspend = 0x0007;
        private readonly Win32MessageOnlyWindow _window = new("Moment lifecycle events");
        private string _timeZoneId = TimeZoneInfo.Local.Id;
        private bool _started;
        private bool _disposed;

        public PowerBroadcastWindow() => _window.MessageReceived += OnMessageReceived;

        public event EventHandler? Unlocked;
        public event EventHandler? PowerResumed;
        public event EventHandler<ResumeReason>? ClockChanged;

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
                Unlocked?.Invoke(this, EventArgs.Empty);
            else if (message == WmPowerBroadcast &&
                wParam.ToInt32() is ResumeAutomatic or ResumeSuspend)
                PowerResumed?.Invoke(this, EventArgs.Empty);
            else if (message == WmTimeChange)
            {
                TimeZoneInfo.ClearCachedData();
                var currentTimeZoneId = TimeZoneInfo.Local.Id;
                var reason = string.Equals(currentTimeZoneId, _timeZoneId, StringComparison.Ordinal)
                    ? ResumeReason.TimeChanged
                    : ResumeReason.TimeZoneChanged;
                _timeZoneId = currentTimeZoneId;
                ClockChanged?.Invoke(this, reason);
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
