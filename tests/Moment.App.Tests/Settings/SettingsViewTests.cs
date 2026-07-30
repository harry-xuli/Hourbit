using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moment.App.Settings;
using Moment.Core.Abstractions;
using Moment.Windows.Hotkeys;

namespace Moment.App.Tests.Settings;

public sealed class SettingsViewTests
{
    [Fact]
    public Task Primary_settings_controls_follow_visible_keyboard_order() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            view.Show();
            view.Activate();
            view.UpdateLayout();
            var hotkey = Assert.IsType<TextBox>(view.FindName("HotkeyBox"));
            var saveHotkey = Assert.IsType<Button>(
                view.FindName("SaveHotkeyButton"));
            var startup = Assert.IsType<CheckBox>(
                view.FindName("StartupCheckBox"));
            Assert.True(hotkey.Focus());

            Assert.True(hotkey.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.Same(saveHotkey, Keyboard.FocusedElement);
            Assert.True(saveHotkey.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.Same(startup, Keyboard.FocusedElement);
        });

    [Fact]
    public Task Normal_and_important_default_levels_have_distinct_keyboard_actions() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            view.Show();
            view.UpdateLayout();
            var normal = Assert.IsType<Button>(
                view.FindName("TestNormalNotificationButton"));
            var important = Assert.IsType<Button>(
                view.FindName("TestImportantAlertButton"));

            Assert.True(normal.IsEnabled);
            Assert.True(important.IsEnabled);
            Assert.Equal("发送测试通知",
                System.Windows.Automation.AutomationProperties.GetName(normal));
            Assert.Equal("测试重要提醒",
                System.Windows.Automation.AutomationProperties.GetName(important));
        });

    [Fact]
    public Task Settings_remain_scrollable_in_a_200_percent_equivalent_viewport() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            view.SizeToContent = SizeToContent.Manual;
            view.Height = 360;
            view.Show();
            view.UpdateLayout();

            var scroller = Assert.IsType<ScrollViewer>(
                view.FindName("SettingsScrollViewer"));
            Assert.True(scroller.ScrollableHeight > 0);
        });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task Save_action_remains_reachable_at_simulated_display_scale(
        double scale) =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            view.SizeToContent = SizeToContent.Manual;
            view.Width = 820;
            view.Height = 620;
            var content = Assert.IsType<StackPanel>(
                view.FindName("SettingsContent"));
            content.LayoutTransform = new ScaleTransform(scale, scale);
            view.Show();
            view.UpdateLayout();
            var scroller = Assert.IsType<ScrollViewer>(
                view.FindName("SettingsScrollViewer"));
            var save = Assert.IsType<Button>(
                view.FindName("SaveSettingsButton"));

            save.BringIntoView();
            view.Dispatcher.Invoke(
                () => { }, DispatcherPriority.ApplicationIdle);
            var bounds = save.TransformToAncestor(scroller).TransformBounds(
                new Rect(save.RenderSize));

            Assert.True(bounds.Bottom <= scroller.ViewportHeight + 1);
            Assert.True(bounds.Top >= -1);
        });

    [Fact]
    public Task Simulated_high_contrast_palette_reaches_settings_controls() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            ApplyHighContrastPalette(view);
            view.Show();
            view.UpdateLayout();

            AssertBrush(Colors.Black, view.Background);
            var hotkey = Assert.IsType<TextBox>(view.FindName("HotkeyBox"));
            AssertBrush(Colors.Black, hotkey.Background);
            AssertBrush(Colors.White, hotkey.Foreground);
            var primary = Assert.IsType<Button>(
                view.FindName("SaveHotkeyButton"));
            AssertBrush(Colors.Yellow, primary.Background);
            AssertBrush(Colors.Black, primary.Foreground);
            var secondary = Assert.IsType<Button>(
                view.FindName("TestNormalNotificationButton"));
            AssertBrush(Colors.Black, secondary.Background);
            AssertBrush(Colors.White, secondary.Foreground);
        });

    private static SettingsView CreateView() =>
        new()
        {
            DataContext = new SettingsViewModel(
                new StubHotkeys(), new ViewSettingsStore())
        };

    private sealed class StubHotkeys : IGlobalHotkeyService
    {
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }
        public HotkeyRegistrationResult Register(string gesture) =>
            HotkeyRegistrationResult.Registered;
        public void Dispose() { }
    }

    private sealed class ViewSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new AppSettings(
                "Ctrl+Alt+Space", false, 100, null));
        public Task SaveAsync(AppSettings settings, CancellationToken ct) =>
            Task.CompletedTask;
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
