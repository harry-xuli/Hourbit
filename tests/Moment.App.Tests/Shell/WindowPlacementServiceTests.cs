using System.Windows;
using Moment.App.Shell;

namespace Moment.App.Tests.Shell;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public Task Target_monitor_dpi_converts_physical_work_area_before_centering() =>
        WpfTestHost.RunAsync(() =>
        {
            var service = new WindowPlacementService(() =>
                new MonitorGeometry(
                    new Rect(1920, 0, 1920, 1080), 2, 2));
            var window = new Window
            {
                Width = 800,
                Height = 400,
                ShowInTaskbar = false
            };

            service.Place(window);

            Assert.Equal(1040, window.Left);
            Assert.Equal(70, window.Top);
            Assert.Equal(800, window.Width);
            Assert.Equal(400, window.Height);
        });

    [Fact]
    public Task Constrained_work_area_clamps_fixed_settings_sized_window()
        => WpfTestHost.RunAsync(() =>
        {
            var service = new WindowPlacementService(() =>
                new MonitorGeometry(
                    new Rect(100, 80, 500, 400), 1, 1));
            var window = new Window
            {
                Width = 820,
                Height = 760,
                MinWidth = 620,
                MinHeight = 480,
                ShowInTaskbar = false
            };

            service.Place(window);

            Assert.Equal(500, window.Width);
            Assert.Equal(400, window.Height);
            Assert.Equal(100, window.Left);
            Assert.Equal(80, window.Top);
        });
}
