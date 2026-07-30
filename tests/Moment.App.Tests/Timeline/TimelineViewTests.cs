using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

public sealed class TimelineViewTests
{
    private static readonly string[] ExpectedGroups = ["已错过", "接下来", "已完成"];

    [Fact]
    public Task Empty_timeline_renders_all_fixed_group_headers_in_required_order() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(new QueryStub());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);

            var groupList = Assert.IsType<ItemsControl>(view.FindName("GroupList"));
            Assert.Equal(3, groupList.Items.Count);
            Assert.Equal(ExpectedGroups, VisibleGroupHeaders(view));
        });

    [Fact]
    public Task Partial_timeline_keeps_empty_groups_visible_and_row_lists_virtualized() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(new QueryStub(
                TestData.Row("已完成复盘", "2026-07-29T08:00:00+08:00",
                    OccurrenceState.Completed)));
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);

            Assert.Equal(ExpectedGroups, VisibleGroupHeaders(view));
            Assert.Contains("已完成复盘", VisibleText(view));
            var rowLists = Descendants<ListBox>(view).ToArray();
            Assert.Equal(3, rowLists.Length);
            Assert.All(rowLists, list =>
            {
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(list));
            });
            Assert.Equal([0, 0, 1], rowLists.Select(list => list.Items.Count));
        });

    private static TimelineView Show(TimelineViewModel viewModel)
    {
        var view = new TimelineView { DataContext = viewModel };
        var window = new Window
        {
            Content = view,
            Width = 900,
            Height = 600,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        window.UpdateLayout();
        return view;
    }

    private static IEnumerable<string> VisibleText(DependencyObject root) =>
        Descendants<TextBlock>(root)
            .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
            .Select(text => text.Text);

    private static string[] VisibleGroupHeaders(DependencyObject root) =>
        Descendants<TextBlock>(root)
            .Where(text => text.IsVisible
                && System.Windows.Automation.AutomationProperties.GetName(text)
                    .StartsWith("时间线分组：", StringComparison.Ordinal))
            .Select(text => text.Text)
            .ToArray();

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

    private static TimelineViewModel Create(ITimelineQuery query) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"),
            new ReminderServiceStub(), new ActionServiceStub(), new DialogStub(),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-view", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));

    private sealed class QueryStub(params TimelineRow[] rows) : ITimelineQuery
    {
        public Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TimelineRow>>(rows);
    }

    private sealed class ReminderServiceStub : IReminderService
    {
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ActionServiceStub : IReminderActionService
    {
        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) => Task.CompletedTask;
        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) => Task.CompletedTask;
        public Task<ReminderOccurrence> SnoozeAsync(
            Guid occurrenceId, TimeSpan delay, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    private sealed class DialogStub : ITimelineDialogService
    {
        public Task<SeriesScope?> SelectEditScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<SeriesScope?> SelectDeleteScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<ReminderDraft?>(null);
        public void OpenQuickAdd() { }
    }
}
