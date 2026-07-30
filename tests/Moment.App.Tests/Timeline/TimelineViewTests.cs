using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
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
    public Task Simulated_dark_system_palette_reaches_timeline_surfaces() =>
        WpfTestHost.RunAsync(() =>
        {
            var view = new TimelineView();
            ApplyDarkPalette(view);
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false
            };
            window.Show();
            window.UpdateLayout();

            AssertBrush(Colors.Black, view.Background);
            AssertBrush(Colors.White, view.Foreground);
            var header = Assert.IsType<Border>(
                view.FindName("TimelineHeader"));
            AssertBrush(Colors.DarkSlateGray, header.Background);
            window.Close();
        });

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
    public Task Timeline_focus_moves_from_new_action_to_a_row_and_Enter_is_edit_command() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = CreateTraversalViewModel();
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var create = Assert.IsType<Button>(
                view.FindName("NewReminderButton"));
            Assert.True(create.Focus());

            Assert.True(create.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next)));
            var focused = Assert.IsAssignableFrom<DependencyObject>(
                Keyboard.FocusedElement);
            var list = focused as ListBox ?? Ancestor<ListBox>(focused);
            Assert.True(list is not null,
                $"Focused element was {focused.GetType().FullName}.");
            Assert.NotNull(list.SelectedItem);

            var enter = Assert.Single(
                view.InputBindings.OfType<KeyBinding>(),
                binding => binding.Key == Key.Enter);
            Assert.Same(viewModel.EditCommand, enter.Command);
            Assert.True(enter.Command.CanExecute(enter.CommandParameter));
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

    [Fact]
    public Task Initial_view_model_selection_is_visible_in_exactly_one_group() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(TwoGroupQuery());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);

            var selected = Descendants<ListBox>(view)
                .SelectMany(list => list.SelectedItems.Cast<TimelineItemViewModel>())
                .ToArray();

            var item = Assert.Single(selected);
            Assert.Same(viewModel.SelectedItem, item);
            Assert.Equal("已错过事项", item.Title);
        });

    [Fact]
    public Task Selecting_an_item_in_another_group_clears_the_previous_group_selection() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(TwoGroupQuery());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var rowLists = Descendants<ListBox>(view).ToArray();
            var missed = viewModel.Items.Single(item => item.Title == "已错过事项");
            var upcoming = viewModel.Items.Single(item => item.Title == "接下来事项");
            var missedList = rowLists.Single(list => list.Items.Contains(missed));
            var upcomingList = rowLists.Single(list => list.Items.Contains(upcoming));

            missedList.SelectedItem = missed;
            upcomingList.SelectedItem = upcoming;

            Assert.Same(upcoming, viewModel.SelectedItem);
            Assert.Null(missedList.SelectedItem);
            Assert.Same(upcoming, Assert.Single(rowLists
                .SelectMany(list => list.SelectedItems.Cast<TimelineItemViewModel>())));
        });

    [Fact]
    public Task Deselecting_the_active_row_clears_the_command_target() =>
        WpfTestHost.RunAsync(() =>
        {
            var actions = new ActionServiceStub();
            var viewModel = Create(TwoGroupQuery(), actions);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var selectedItem = Assert.IsType<TimelineItemViewModel>(viewModel.SelectedItem);
            var selectedList = Descendants<ListBox>(view)
                .Single(list => ReferenceEquals(list.SelectedItem, selectedItem));

            selectedList.SelectedItem = null;

            Assert.Null(viewModel.SelectedItem);
            Assert.Empty(Descendants<ListBox>(view)
                .SelectMany(list => list.SelectedItems.Cast<TimelineItemViewModel>()));
            Assert.False(viewModel.EditCommand.CanExecute(null));
            Assert.False(viewModel.DeleteCommand.CanExecute(null));
            Assert.False(viewModel.CompleteCommand.CanExecute(null));
            viewModel.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Empty(actions.CompletedOccurrenceIds);
        });

    [Fact]
    public Task Complete_command_targets_the_single_selection_projected_from_the_view_model() =>
        WpfTestHost.RunAsync(() =>
        {
            var actions = new ActionServiceStub();
            var viewModel = Create(TwoGroupQuery(), actions);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var upcoming = viewModel.Items.Single(item => item.Title == "接下来事项");

            viewModel.SelectedItem = upcoming;

            var visibleSelection = Assert.Single(Descendants<ListBox>(view)
                .SelectMany(list => list.SelectedItems.Cast<TimelineItemViewModel>()));
            Assert.Same(upcoming, visibleSelection);
            viewModel.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal([upcoming.OccurrenceId], actions.CompletedOccurrenceIds);
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

    private static T? Ancestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static QueryStub TwoGroupQuery() =>
        new(
            TestData.Row("已错过事项", "2026-07-29T08:00:00+08:00",
                OccurrenceState.Missed),
            TestData.Row("接下来事项", "2026-07-29T10:00:00+08:00"));

    private static TimelineViewModel Create(
        ITimelineQuery query,
        ActionServiceStub? actions = null) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"),
            new ReminderServiceStub(), actions ?? new ActionServiceStub(), new DialogStub(),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-view", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));

    private static TimelineViewModel CreateTraversalViewModel()
    {
        var now = new DateTimeOffset(
            2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
        var query = new QueryStub(
            new TimelineRow(
                Guid.NewGuid(), "已错过事项", now.AddHours(-1),
                ReminderKind.Plan, ReminderImportance.Normal,
                OccurrenceState.Missed, null),
            new TimelineRow(
                Guid.NewGuid(), "接下来事项", now.AddHours(1),
                ReminderKind.Plan, ReminderImportance.Normal,
                OccurrenceState.Scheduled, null));
        return new TimelineViewModel(
            query, new LocalClock(now), new ReminderServiceStub(),
            new ActionServiceStub(), new DialogStub(),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-traversal", TimeSpan.FromHours(8),
                "UTC+08", "UTC+08"));
    }

    private static void ApplyDarkPalette(FrameworkElement element)
    {
        element.Resources[SystemColors.WindowBrushKey] =
            new SolidColorBrush(Colors.Black);
        element.Resources[SystemColors.WindowTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.ControlBrushKey] =
            new SolidColorBrush(Colors.DarkSlateGray);
        element.Resources[SystemColors.ControlTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.ActiveBorderBrushKey] =
            new SolidColorBrush(Colors.Yellow);
        element.Resources[SystemColors.HighlightBrushKey] =
            new SolidColorBrush(Colors.Yellow);
        element.Resources[SystemColors.HighlightTextBrushKey] =
            new SolidColorBrush(Colors.Black);
    }

    private static void AssertBrush(Color expected, Brush actual) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(actual).Color);

    private sealed class QueryStub(params TimelineRow[] rows) : ITimelineQuery
    {
        public Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TimelineRow>>(rows);
    }

    private sealed class LocalClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
        public Task DelayUntilAsync(
            DateTimeOffset dueAt,
            CancellationToken ct) => Task.CompletedTask;
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
        public List<Guid> CompletedOccurrenceIds { get; } = [];

        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct)
        {
            CompletedOccurrenceIds.Add(occurrenceId);
            return Task.CompletedTask;
        }

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
