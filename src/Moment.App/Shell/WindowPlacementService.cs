using System.Windows;
using System.Windows.Media;

namespace Moment.App.Shell;

public sealed class WindowPlacementService
{
    private readonly Func<Window, Rect> _workingArea;

    public WindowPlacementService()
        : this(CurrentWorkingArea)
    {
    }

    public WindowPlacementService(Func<Rect> workingArea)
        : this(_ => workingArea())
    {
    }

    private WindowPlacementService(Func<Window, Rect> workingArea) =>
        _workingArea = workingArea;

    public void Place(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var area = _workingArea(window);
        if (area.Width <= 0 || area.Height <= 0)
            return;

        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        if (!double.IsNaN(window.Width) && window.Width > area.Width)
            window.Width = area.Width;
        if (!double.IsNaN(window.Height) && window.Height > area.Height)
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

    private static Rect CurrentWorkingArea(Window window)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            System.Windows.Forms.Control.MousePosition);
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);
    }
}
