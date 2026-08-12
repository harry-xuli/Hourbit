using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Moment.App.Help;

namespace Moment.App.Tests.Help;

public sealed class HelpWindowTests
{
    [Fact]
    public Task Controller_reuses_the_visible_window_and_reopens_after_close() =>
        WpfTestHost.RunAsync(() =>
        {
            var created = new List<HelpWindow>();
            using var controller = new HelpWindowController(() =>
            {
                var window = new HelpWindow { ShowInTaskbar = false };
                created.Add(window);
                return window;
            });

            controller.ShowAndFocus();
            controller.ShowAndFocus();
            Assert.Single(created);

            created[0].Close();
            controller.ShowAndFocus();
            Assert.Equal(2, created.Count);
        });

    [Fact]
    public Task Help_window_packages_the_required_usage_topics() =>
        WpfTestHost.RunAsync(() =>
        {
            var window = new HelpWindow { ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();

            var text = string.Join(" ", Descendants<TextBlock>(window)
                .Select(block => block.Text));
            Assert.Contains("快速创建", text);
            Assert.Contains("无时间", text);
            Assert.Contains("5点", text);
            Assert.Contains("下午5点", text);
            Assert.Contains("每天", text);
            Assert.Contains("工作日", text);
            Assert.Contains("每周", text);
            Assert.Contains("完成", text);
            Assert.Contains("忽略", text);
            Assert.Contains("稍后提醒", text);
            Assert.Contains("Ctrl+D", text);
            Assert.Contains("倒计时", text);
            Assert.Contains("升级", text);
            Assert.Contains("托盘", text);
            window.Close();
        });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
