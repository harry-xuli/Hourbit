using System.Windows;
using Hourbit.App.Shell;

namespace Hourbit.App.Tests.Shell;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public Task Target_monitor_origin_remains_physical_at_200_percent_dpi() =>
        WpfTestHost.RunAsync(() =>
        {
            var positioner = new RecordingPositioner();
            var service = new WindowPlacementService(() =>
                new MonitorGeometry(
                    new Rect(1920, 0, 1920, 1080), 2, 2),
                positioner);
            var window = new Window
            {
                Width = 800,
                Height = 400,
                ShowInTaskbar = false
            };

            service.Place(window);

            Assert.Equal(
                new PhysicalWindowBounds(2080, 140, 1600, 800),
                positioner.Bounds);
            Assert.Equal(800, window.Width);
            Assert.Equal(400, window.Height);
        });

    [Fact]
    public Task Negative_target_monitor_origin_remains_physical() =>
        WpfTestHost.RunAsync(() =>
        {
            var positioner = new RecordingPositioner();
            var service = new WindowPlacementService(() =>
                new MonitorGeometry(
                    new Rect(-1920, -200, 1920, 1080), 1.5, 1.5),
                positioner);
            var window = new Window
            {
                Width = 800,
                Height = 400,
                ShowInTaskbar = false
            };

            service.Place(window);

            Assert.Equal(
                new PhysicalWindowBounds(-1560, 40, 1200, 600),
                positioner.Bounds);
        });

    [Fact]
    public Task Constrained_work_area_clamps_fixed_settings_sized_window()
        => WpfTestHost.RunAsync(() =>
        {
            var positioner = new RecordingPositioner();
            var service = new WindowPlacementService(() =>
                new MonitorGeometry(
                    new Rect(100, 80, 500, 400), 1, 1),
                positioner);
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
            Assert.Equal(
                new PhysicalWindowBounds(100, 80, 500, 400),
                positioner.Bounds);
        });

    private sealed class RecordingPositioner : IPhysicalWindowPositioner
    {
        public PhysicalWindowBounds? Bounds { get; private set; }

        public void SetPosition(Window window, PhysicalWindowBounds bounds) =>
            Bounds = bounds;
    }
}
