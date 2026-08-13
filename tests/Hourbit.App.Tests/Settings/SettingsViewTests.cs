using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using Hourbit.App.Settings;
using Hourbit.App.Styles;
using Hourbit.Core.Abstractions;
using Hourbit.Windows.Hotkeys;
using Hourbit.Windows.Alerts;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Settings;

public sealed class SettingsViewTests
{
    [Fact]
    public void Manual_backup_export_uses_the_hourbit_public_prefix()
    {
        var name = SettingsView.CreateBackupExportFileName(
            DateTimeOffset.Parse("2026-08-08T11:12:13Z"));

        Assert.Equal(
            "hourbit-export-20260808T111213Z.moment-backup",
            name);
    }

    [Fact]
    public Task Every_settings_action_follows_visible_keyboard_order() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = CreateView();
            view.Show();
            view.Activate();
            view.UpdateLayout();
            var hotkey = Assert.IsType<TextBox>(view.FindName("HotkeyBox"));
            Assert.True(hotkey.Focus());

            var expected = new[]
            {
                "全局快捷键",
                "测试并保存快捷键",
                "开机启动",
                "发送测试通知",
                "测试重要提醒",
                "自定义 WAV 声音路径",
                "选择 WAV 声音文件",
                "播放声音预览",
                "停止声音预览",
                "提醒音量 0 到 100",
                "打开数据文件夹",
                "打开备份文件夹",
                "立即创建备份",
                "导出备份",
                "从备份恢复",
                "保存设置"
            };
            var actual = new List<string>();
            foreach (var _ in expected)
            {
                var focused = Assert.IsAssignableFrom<UIElement>(
                    Keyboard.FocusedElement);
                actual.Add(System.Windows.Automation.AutomationProperties
                    .GetName(focused));
                if (actual.Count < expected.Length)
                {
                    Assert.True(focused.MoveFocus(
                        new TraversalRequest(
                            FocusNavigationDirection.Next)));
                }
            }

            Assert.Equal(expected, actual);
        });

    [Fact]
    public Task Normal_and_important_behavior_tests_have_distinct_keyboard_actions() =>
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
            var backup = Assert.IsType<Button>(
                view.FindName("OpenBackupFolderButton"));
            Assert.Equal("打开备份文件夹",
                System.Windows.Automation.AutomationProperties.GetName(backup));
            Assert.Contains(Descendants<TextBlock>(backup),
                text => text.Text == "打开备份文件夹");
        });

    [Fact]
    public Task Open_backup_folder_label_invokes_the_backups_subfolder_action() =>
        WpfTestHost.RunAsync(() =>
        {
            string? opened = null;
            var dataFolder = AppContext.BaseDirectory;
            var view = new SettingsView(
                new SettingsViewModel(
                    new StubHotkeys(), new ViewSettingsStore()),
                new SettingsViewActions(
                    new NoopAudio(),
                    _ => Task.CompletedTask,
                    _ => Task.CompletedTask,
                    dataFolder,
                    path => opened = path));
            view.Show();
            view.UpdateLayout();
            var backup = Assert.IsType<Button>(
                view.FindName("OpenBackupFolderButton"));

            backup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(
                Path.Combine(dataFolder, "backups"),
                opened);
        });

    [Fact]
    public Task Restore_requires_a_selected_file_and_explicit_confirmation() =>
        WpfTestHost.RunAsync(() =>
        {
            using var temp = new TempDirectory();
            var backupPath = Path.Combine(temp.Path, "chosen.moment-backup");
            File.WriteAllBytes(backupPath, [1]);
            var backup = new RecordingBackupService();
            var confirmations = 0;
            var view = new SettingsView(
                new SettingsViewModel(
                    new StubHotkeys(),
                    new ViewSettingsStore(),
                    backupService: backup),
                new SettingsViewActions(
                    new NoopAudio(),
                    _ => Task.CompletedTask,
                    _ => Task.CompletedTask,
                    temp.Path,
                    SelectBackupRestorePath: () => backupPath,
                    ConfirmRestore: path =>
                    {
                        confirmations++;
                        Assert.Equal(backupPath, path);
                        return false;
                    }));
            view.Show();
            view.UpdateLayout();

            Assert.IsType<Button>(view.FindName("RestoreBackupButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Dispatcher.Invoke(
                () => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(1, confirmations);
            Assert.Empty(backup.RestoredPaths);
        });

    [Fact]
    public Task Release_page_button_is_hidden_without_valid_https_metadata() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = new SettingsView(
                new SettingsViewModel(
                    new StubHotkeys(),
                    new ViewSettingsStore(),
                    releasePage: new ReleasePageService("http://example.test/releases")),
                new SettingsViewActions(
                    new NoopAudio(),
                    _ => Task.CompletedTask,
                    _ => Task.CompletedTask,
                    AppContext.BaseDirectory));
            view.Show();
            view.UpdateLayout();

            var update = Assert.IsType<Button>(
                view.FindName("CheckForUpdatesButton"));

            Assert.Equal(Visibility.Collapsed, update.Visibility);
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
            HighContrastPalette.Apply(view.Resources, true, view.FindResource);
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

    private sealed class NoopAudio : IImportantAlertAudio
    {
        public Task StartCustomLoopAsync(
            string audioPath,
            CancellationToken ct) => Task.CompletedTask;
        public Task StartDefaultLoopAsync(CancellationToken ct) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingBackupService : Hourbit.Infrastructure.Backup.IBackupService
    {
        public List<string> RestoredPaths { get; } = [];
        public Task<string> CreateDailyBackupAsync(CancellationToken ct) =>
            Task.FromResult("created");
        public Task ExportAsync(string destinationPath, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RestoreAsync(string backupPath, CancellationToken ct)
        {
            RestoredPaths.Add(backupPath);
            return Task.CompletedTask;
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
