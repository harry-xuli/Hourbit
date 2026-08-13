using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hourbit.App.Shell;

public sealed record MonitorGeometry(
    Rect PhysicalWorkingArea,
    double DpiScaleX,
    double DpiScaleY)
{
    public System.Windows.Size DeviceIndependentWorkingAreaSize()
    {
        if (DpiScaleX <= 0 || DpiScaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(DpiScaleX));
        return new System.Windows.Size(
            PhysicalWorkingArea.Width / DpiScaleX,
            PhysicalWorkingArea.Height / DpiScaleY);
    }
}

internal readonly record struct PhysicalWindowBounds(
    int X, int Y, int Width, int Height);

internal interface IPhysicalWindowPositioner
{
    void SetPosition(Window window, PhysicalWindowBounds bounds);
}

public sealed class WindowPlacementService
{
    private readonly Func<MonitorGeometry> _targetMonitor;
    private readonly IPhysicalWindowPositioner _positioner;

    public WindowPlacementService()
        : this(CurrentTargetMonitor, new NativePhysicalWindowPositioner())
    {
    }

    public WindowPlacementService(Func<Rect> workingArea)
        : this(
            () => new MonitorGeometry(workingArea(), 1, 1),
            new DeviceIndependentWindowPositioner())
    {
    }

    public WindowPlacementService(Func<MonitorGeometry> targetMonitor)
        : this(targetMonitor, new NativePhysicalWindowPositioner())
    {
    }

    internal WindowPlacementService(
        Func<MonitorGeometry> targetMonitor,
        IPhysicalWindowPositioner positioner)
    {
        _targetMonitor = targetMonitor ??
            throw new ArgumentNullException(nameof(targetMonitor));
        _positioner = positioner ??
            throw new ArgumentNullException(nameof(positioner));
    }

    public void Place(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var monitor = _targetMonitor();
        var physicalArea = monitor.PhysicalWorkingArea;
        var available = monitor.DeviceIndependentWorkingAreaSize();
        if (available.Width <= 0 || available.Height <= 0)
            return;

        window.MinWidth = Math.Min(window.MinWidth, available.Width);
        window.MinHeight = Math.Min(window.MinHeight, available.Height);
        window.MaxWidth = available.Width;
        window.MaxHeight = available.Height;
        if (double.IsNaN(window.Width) || window.Width > available.Width)
            window.Width = available.Width;
        if (double.IsNaN(window.Height) || window.Height > available.Height)
            window.Height = available.Height;

        window.UpdateLayout();
        var widthDip = Math.Min(
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            available.Width);
        var heightDip = Math.Min(
            window.ActualHeight > 0 ? window.ActualHeight : window.Height,
            available.Height);
        var width = Math.Min(
            (int)Math.Round(widthDip * monitor.DpiScaleX),
            (int)Math.Round(physicalArea.Width));
        var height = Math.Min(
            (int)Math.Round(heightDip * monitor.DpiScaleY),
            (int)Math.Round(physicalArea.Height));
        var left = (int)Math.Round(
            physicalArea.Left + ((physicalArea.Width - width) / 2));
        var top = (int)Math.Round(
            physicalArea.Top + ((physicalArea.Height - height) / 2));

        _positioner.SetPosition(
            window,
            new PhysicalWindowBounds(left, top, width, height));
    }

    private static MonitorGeometry CurrentTargetMonitor()
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        var monitor = NativeMethods.MonitorFromPoint(
            new Point(cursor.X, cursor.Y), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException(
                "Windows could not read the target monitor work area.");

        var scaleX = 1d;
        var scaleY = 1d;
        if (NativeMethods.GetDpiForMonitor(
                monitor, 0, out var dpiX, out var dpiY) == 0)
        {
            scaleX = dpiX / 96d;
            scaleY = dpiY / 96d;
        }

        return new MonitorGeometry(
            new Rect(
                info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top),
            scaleX,
            scaleY);
    }

    private sealed class NativePhysicalWindowPositioner : IPhysicalWindowPositioner
    {
        public void SetPosition(Window window, PhysicalWindowBounds bounds)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "The window must be shown before it can be positioned.");
            if (!NativeMethods.SetWindowPos(
                    handle, IntPtr.Zero, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height, 0x0004 | 0x0010))
            {
                throw new InvalidOperationException(
                    "Windows could not position the window on the target monitor.");
            }
        }
    }

    private sealed class DeviceIndependentWindowPositioner
        : IPhysicalWindowPositioner
    {
        public void SetPosition(Window window, PhysicalWindowBounds bounds)
        {
            window.Left = bounds.X;
            window.Top = bounds.Y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);
    }
}
