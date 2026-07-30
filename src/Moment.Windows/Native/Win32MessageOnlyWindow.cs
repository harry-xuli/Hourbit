using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Moment.Windows.Native;

internal enum Win32WindowMode
{
    MessageOnly,
    HiddenTopLevel
}

/// <summary>Owns a Win32 native window and its dedicated message-loop thread.</summary>
internal sealed class Win32MessageOnlyWindow : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private static readonly IntPtr MessageOnlyParent = new(-3);
    private static readonly ConcurrentDictionary<IntPtr, Win32MessageOnlyWindow> Windows = new();
    private static readonly NativeMethods.WindowProcedure Procedure = WindowProcedure;
    private readonly string _className = $"Moment.NativeWindow.{Guid.NewGuid():N}";
    private readonly Win32WindowMode _mode;
    private readonly TaskCompletionSource<IntPtr> _created =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private IntPtr _handle;
    private int _disposed;

    public Win32MessageOnlyWindow(
        string threadName,
        Win32WindowMode mode = Win32WindowMode.MessageOnly)
    {
        _mode = mode;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = threadName
        };
        _thread.Start();
        if (!_created.Task.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The Win32 message window could not be created.");
        _handle = _created.Task.GetAwaiter().GetResult();
    }

    public IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _handle;
        }
    }

    public event Action<int, IntPtr, IntPtr>? MessageReceived;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var handle = _handle;
        if (handle != IntPtr.Zero)
            NativeMethods.PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(5));
        _handle = IntPtr.Zero;
    }

    private void MessageLoop()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            Instance = instance,
            Procedure = Procedure,
            ClassName = _className
        };
        var atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            _created.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        try
        {
            var handle = NativeMethods.CreateWindowEx(
                0,
                _className,
                _className,
                0,
                0,
                0,
                0,
                0,
                _mode == Win32WindowMode.MessageOnly ? MessageOnlyParent : IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                _created.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }

            _handle = handle;
            Windows[handle] = this;
            _created.TrySetResult(handle);
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_handle != IntPtr.Zero)
                Windows.TryRemove(_handle, out _);
            NativeMethods.UnregisterClass(_className, instance);
        }
    }

    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClose)
        {
            NativeMethods.DestroyWindow(window);
            return IntPtr.Zero;
        }
        if (message == WmDestroy)
        {
            Windows.TryRemove(window, out _);
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (Windows.TryGetValue(window, out var owner))
            owner.MessageReceived?.Invoke((int)message, wParam, lParam);
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal IntPtr Instance;
            internal IntPtr Icon;
            internal IntPtr Cursor;
            internal IntPtr Background;
            internal string? MenuName;
            internal string ClassName;
            internal IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMessage
        {
            internal IntPtr Window;
            internal uint Message;
            internal IntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
