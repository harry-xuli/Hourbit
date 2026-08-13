using Hourbit.App.QuickAdd;

namespace Hourbit.App.Tests.QuickAdd;

public sealed class QuickAddWindowControllerTests
{
    [Fact]
    public void Tray_or_hotkey_recreates_the_window_after_the_previous_window_was_closed()
    {
        var windows = new List<Window>();
        var controller = new QuickAddWindowController(() =>
        {
            var window = new Window();
            windows.Add(window);
            return window;
        });

        controller.ShowAndFocus();
        windows[0].Close();
        controller.ShowAndFocus();

        Assert.Equal(2, windows.Count);
        Assert.Equal(1, windows[0].Shows);
        Assert.Equal(1, windows[1].Shows);
        Assert.False(windows[1].IsClosed);
    }

    private sealed class Window : IQuickAddWindow
    {
        public bool IsClosed { get; private set; }
        public int Shows { get; private set; }
        public void ShowAndFocus() => Shows++;
        public void Close() => IsClosed = true;
    }
}
