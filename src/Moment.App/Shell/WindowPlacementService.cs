using System.Runtime.InteropServices;
using System.Windows;

namespace Moment.App.Shell;

public sealed record MonitorGeometry(
    Rect PhysicalWorkingArea,
    double DpiScaleX,
    double DpiScaleY)
{
    public Rect ToDeviceIndependentWorkingArea()
    {
        if (DpiScaleX <= 0 || DpiScaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(DpiScaleX));
        return new Rect(
            PhysicalWorkingArea.Left / DpiScaleX,
            PhysicalWorkingArea.Top / DpiScaleY,
            PhysicalWorkingArea.Width / DpiScaleX,
            PhysicalWorkingArea.Height / DpiScaleY);
    }
}

public sealed class WindowPlacementService
{
    private readonly Func<MonitorGeometry> _targetMonitor;

    public WindowPlacementService()
        : this(CurrentTargetMonitor)
    {
    }

    public WindowPlacementService(Func<Rect> workingArea)
        : this(() => new MonitorGeometry(workingArea(), 1, 1))
    {
    }

    public WindowPlacementService(Func<MonitorGeometry> targetMonitor) =>
        _targetMonitor = targetMonitor ??
            throw new ArgumentNullException(nameof(targetMonitor));

    public void Place(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var area = _targetMonitor().ToDeviceIndependentWorkingArea();
        if (area.Width <= 0 || area.Height <= 0)
            return;

        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        if (double.IsNaN(window.Width) || window.Width > area.Width)
            window.Width = area.Width;
        if (double.IsNaN(window.Height) || window.Height > area.Height)
            window.Height = area.Height;

        window.UpdateLayout();
        var width = Math.Min(
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            area.Width);
        var height = Math.Min(
            window.ActualHeight > 0 ? window.ActualHeight : window.Height,
            area.Height);
        window.Left = Math.Clamp(
            area.Left + ((area.Width - width) / 2),
            area.Left,
            area.Right - width);
        window.Top = Math.Clamp(
            area.Top + ((area.Height - height) / 2),
            area.Top,
            area.Bottom - height);
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
        internal static extern IntPtr MonitorFromPoint(
            Point point,
            uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetMonitorInfoW",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MonitorInfo info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}
