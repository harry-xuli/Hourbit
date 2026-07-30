using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Reflection;
using Moment.App.Alerts;
using Moment.App.Shell;
using Moment.Core.Domain;
using Moment.Windows.Alerts;

namespace Moment.App.Tests.Alerts;

public sealed class ImportantAlertWindowTests
{
    [Fact]
    public void Bundled_default_alert_is_a_real_wave_resource()
    {
        using var wave = typeof(ImportantAlertWindow).Assembly
            .GetManifestResourceStream("Moment.App.Assets.default-alert.wav");
        Assert.NotNull(wave);
        var header = new byte[12];
        Assert.Equal(header.Length, wave.Read(header));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        Assert.True(wave.Length >= 44);
    }

    [Fact]
    public async Task Title_bar_close_snoozes_ten_after_audio_is_stopped_and_disposed()
    {
        var audio = new RecordingAudio();
        Task<ImportantAlertAction>? actionTask = null;

        await WpfTestHost.RunAsync(() =>
        {
            var window = new ImportantAlertWindow(
                CreateAlert(), audio,
                new WindowPlacementService(
                    () => new Rect(80, 60, 900, 700)));
            window.Show();
            window.Dispatcher.Invoke(
                () => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(window.Topmost);
            Assert.Equal(["start"], audio.Calls);

            actionTask = window.Completion;
            window.Close();
        });

        Assert.Equal(ImportantAlertAction.Snooze10, await actionTask!);
        Assert.Equal(["start", "stop", "dispose"], audio.Calls);
    }

    [Theory]
    [InlineData("CompleteButton", ImportantAlertAction.Complete)]
    [InlineData("Snooze5Button", ImportantAlertAction.Snooze5)]
    [InlineData("Snooze10Button", ImportantAlertAction.Snooze10)]
    [InlineData("Snooze30Button", ImportantAlertAction.Snooze30)]
    [InlineData("Snooze60Button", ImportantAlertAction.Snooze60)]
    [InlineData("IgnoreButton", ImportantAlertAction.Ignore)]
    public async Task Every_visible_action_button_returns_its_action_after_audio_cleanup(
        string buttonName,
        ImportantAlertAction expected)
    {
        var audio = new RecordingAudio();
        Task<ImportantAlertAction>? actionTask = null;

        await WpfTestHost.RunAsync(() =>
        {
            var window = new ImportantAlertWindow(
                CreateAlert(), audio,
                new WindowPlacementService(
                    () => new Rect(0, 0, 1280, 720)));
            window.Show();
            window.Dispatcher.Invoke(
                () => { }, DispatcherPriority.ApplicationIdle);
            actionTask = window.Completion;

            var button = Assert.IsType<System.Windows.Controls.Button>(
                window.FindName(buttonName));
            Assert.False(string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetName(button)));
            button.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));
        });

        Assert.Equal(expected, await actionTask!);
        Assert.Equal(["start", "stop", "dispose"], audio.Calls);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task Complete_action_remains_reachable_at_simulated_display_scale(
        double scale) =>
        WpfTestHost.RunAsync(() =>
        {
            var window = new ImportantAlertWindow(
                CreateAlert(), new RecordingAudio(),
                new WindowPlacementService(
                    () => new Rect(0, 0, 900, 700)))
            {
                SizeToContent = SizeToContent.Manual,
                Width = 720,
                Height = 600
            };
            var content = Assert.IsType<System.Windows.Controls.Grid>(
                window.FindName("AlertContent"));
            content.LayoutTransform = new ScaleTransform(scale, scale);
            window.Show();
            window.UpdateLayout();
            var scroller = Assert.IsType<System.Windows.Controls.ScrollViewer>(
                window.FindName("AlertScrollViewer"));
            var complete = Assert.IsType<System.Windows.Controls.Button>(
                window.FindName("CompleteButton"));

            complete.BringIntoView();
            window.Dispatcher.Invoke(
                () => { }, DispatcherPriority.ApplicationIdle);
            var bounds = complete.TransformToAncestor(scroller).TransformBounds(
                new Rect(complete.RenderSize));

            Assert.True(bounds.Bottom <= scroller.ViewportHeight + 1);
            Assert.True(bounds.Top >= -1);
        });

    [Fact]
    public Task Simulated_high_contrast_palette_reaches_alert_actions() =>
        WpfTestHost.RunAsync(() =>
        {
            var window = new ImportantAlertWindow(
                CreateAlert(), new RecordingAudio(),
                new WindowPlacementService(
                    () => new Rect(0, 0, 900, 700)));
            ApplyHighContrastPalette(window);
            window.Show();
            window.UpdateLayout();

            AssertBrush(Colors.Black, window.Background);
            var complete = Assert.IsType<System.Windows.Controls.Button>(
                window.FindName("CompleteButton"));
            AssertBrush(Colors.Yellow, complete.Background);
            AssertBrush(Colors.Black, complete.Foreground);
            var snooze = Assert.IsType<System.Windows.Controls.Button>(
                window.FindName("Snooze10Button"));
            AssertBrush(Colors.Black, snooze.Background);
            AssertBrush(Colors.White, snooze.Foreground);
        });

    private static ReminderAlert CreateAlert() =>
        new(Guid.NewGuid(), "提交项目周报",
            new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8)));

    private sealed class RecordingAudio : IImportantAlertAudio, IAsyncDisposable
    {
        public List<string> Calls { get; } = [];
        public Task StartCustomLoopAsync(string audioPath, CancellationToken ct)
        {
            Calls.Add("start-custom");
            return Task.CompletedTask;
        }
        public Task StartDefaultLoopAsync(CancellationToken ct)
        {
            Calls.Add("start");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Calls.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static void ApplyHighContrastPalette(FrameworkElement element)
    {
        element.Resources[SystemColors.WindowBrushKey] =
            new SolidColorBrush(Colors.Black);
        element.Resources[SystemColors.WindowTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.ControlBrushKey] =
            new SolidColorBrush(Colors.Black);
        element.Resources[SystemColors.ControlTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.GrayTextBrushKey] =
            new SolidColorBrush(Colors.LightGray);
        element.Resources[SystemColors.HighlightBrushKey] =
            new SolidColorBrush(Colors.Yellow);
        element.Resources[SystemColors.HighlightTextBrushKey] =
            new SolidColorBrush(Colors.Black);
    }

    private static void AssertBrush(Color expected, Brush actual) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(actual).Color);
}
