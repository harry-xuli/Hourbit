using System.Runtime.InteropServices;
using Hourbit.Windows.Native;

namespace Hourbit.Windows.Hotkeys;

public enum HotkeyRegistrationResult
{
    Success,
    Registered = Success,
    Conflict
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;
    HotkeyRegistrationResult Register(string gesture);
}

public interface IHotkeyWindow : IDisposable
{
    event EventHandler? HotkeyPressed;
    bool Register(uint modifiers, uint virtualKey);
    void Unregister();
}

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly IHotkeyWindow _window;
    private bool _attempted;
    private (uint Modifiers, uint VirtualKey)? _registeredGesture;
    private bool _disposed;

    public GlobalHotkeyService(IHotkeyWindow? window = null)
    {
        _window = window ?? new MessageOnlyHotkeyWindow();
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    public event EventHandler? Pressed;

    public HotkeyRegistrationResult Register(string gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (modifiers, virtualKey) = HotkeyGestureParser.Parse(gesture);
        var previous = _registeredGesture;
        if (_attempted)
            _window.Unregister();

        _attempted = true;
        if (_window.Register(modifiers, virtualKey))
        {
            _registeredGesture = (modifiers, virtualKey);
            return HotkeyRegistrationResult.Registered;
        }

        _registeredGesture = null;
        if (previous is { } old &&
            _window.Register(old.Modifiers, old.VirtualKey))
        {
            _registeredGesture = old;
        }

        return HotkeyRegistrationResult.Conflict;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.HotkeyPressed -= OnHotkeyPressed;
        if (_attempted)
            _window.Unregister();
        _window.Dispose();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (!_disposed)
            Pressed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A Win32 message-only window. Construct and dispose it on the application's UI thread.</summary>
public sealed class MessageOnlyHotkeyWindow : IHotkeyWindow
{
    private const int HotkeyId = 0x4D4F;
    private const int WmHotkey = 0x0312;
    private readonly Win32MessageOnlyWindow _window = new("Hourbit global hotkey");
    private bool _disposed;

    public MessageOnlyHotkeyWindow()
    {
        _window.MessageReceived += OnMessageReceived;
    }

    public event EventHandler? HotkeyPressed;

    public bool Register(uint modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, modifiers, virtualKey);
    }

    public void Unregister()
    {
        if (!_disposed)
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
    }

    private void OnMessageReceived(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && wParam == new IntPtr(HotkeyId))
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window.MessageReceived -= OnMessageReceived;
        _window.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
