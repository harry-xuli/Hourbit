using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Moment.App.Timeline;
using Moment.App.Styles;
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
    public Task New_reminder_vector_icon_and_label_are_vertically_centered() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(TwoGroupQuery());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var button = Assert.IsType<Button>(view.FindName("NewReminderButton"));
            var content = Assert.IsType<StackPanel>(button.Content);
            var icon = Assert.IsType<Viewbox>(content.Children[0]);
            var label = Assert.IsType<TextBlock>(content.Children[1]);

            Assert.Equal(16d, icon.ActualWidth);
            Assert.Equal(16d, icon.ActualHeight);
            Assert.Equal("新建提醒", label.Text);
            Assert.Equal(10d, icon.Margin.Right);

            var iconCenter = icon.TranslatePoint(
                new Point(icon.ActualWidth / 2d, icon.ActualHeight / 2d), button).Y;
            var labelCenter = label.TranslatePoint(
                new Point(label.ActualWidth / 2d, label.ActualHeight / 2d), button).Y;
            Assert.InRange(Math.Abs(iconCenter - labelCenter), 0d, 0.5d);
        });

    [Fact]
    public Task Simulated_dark_system_palette_reaches_timeline_surfaces() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(TwoGroupQuery());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = new TimelineView { DataContext = viewModel };
            ApplyDarkPalette(view);
            HighContrastPalette.Apply(view.Resources, true, view.FindResource);
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
            var headerText = Assert.IsType<TextBlock>(
                view.FindName("TimelineHeaderText"));
            var footerText = Assert.IsType<TextBlock>(
                view.FindName("TimelineFooterText"));
            AssertBrush(Colors.White, headerText.Foreground);
            AssertBrush(Colors.White, footerText.Foreground);

            var selectedList = Descendants<ListBox>(view)
                .Single(list => list.SelectedItem is not null);
            var selected = Assert.IsType<ListBoxItem>(
                selectedList.ItemContainerGenerator.ContainerFromItem(
                    selectedList.SelectedItem));
            var selectedSurface = Assert.Single(
                Descendants<Border>(selected),
                border => border.TemplatedParent == selected);
            AssertBrush(Colors.Yellow, selectedSurface.Background);
            AssertBrush(Colors.Black, selected.Foreground);
            Assert.All(
                Descendants<TextBlock>(selected).Where(text => text.IsVisible),
                text => AssertBrush(Colors.Black, text.Foreground));
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
    public Task Split_timeline_renders_accessible_todos_above_reminders_and_collapses_completed_todos() =>
        WpfTestHost.RunAsync(() =>
        {
            var query = new QueryStub(new TimelineSnapshot(
                [
                    TodoRow("逾期任务", new DateOnly(2026, 7, 28),
                        importance: ReminderImportance.Important),
                    TodoRow("无日期任务", null),
                    TodoRow("完成任务", null, true,
                        DateTimeOffset.Parse("2026-07-29T08:00:00+08:00"))
                ],
                [TestData.Row("定时提醒", "2026-07-29T10:00:00+08:00")],
                1,
                0));
            var viewModel = Create(query);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var todoHeader = Assert.IsType<TextBlock>(
                view.FindName("TodoSectionHeader"));
            var reminderHeader = Assert.IsType<TextBlock>(
                view.FindName("ReminderSectionHeader"));
            var completed = Assert.IsType<Expander>(
                view.FindName("CompletedTodosExpander"));
            var completedSummary = Assert.IsType<StackPanel>(
                view.FindName("CompletedSummary"));

            Assert.Equal("待办事项", todoHeader.Text);
            Assert.Equal("待办事项", System.Windows.Automation.AutomationProperties.GetName(todoHeader));
            Assert.Equal("定时提醒", reminderHeader.Text);
            Assert.Equal("定时提醒", System.Windows.Automation.AutomationProperties.GetName(reminderHeader));
            Assert.True(todoHeader.TranslatePoint(new Point(), view).Y <
                        reminderHeader.TranslatePoint(new Point(), view).Y);
            Assert.False(completed.IsExpanded);
            Assert.Equal("待办：1，提醒：0", completedSummary.ToolTip);
            Assert.Contains("已逾期", VisibleText(view));
            Assert.Contains("重要", VisibleText(view));
            Assert.Contains("无日期", VisibleText(view));
            Assert.DoesNotContain("完成任务", VisibleText(view));
        });

    [Fact]
    public Task Todo_row_receives_keyboard_focus_and_commands_target_the_todo_type() =>
        WpfTestHost.RunAsync(() =>
        {
            var todo = TodoRow("键盘待办", new DateOnly(2026, 7, 29));
            var query = new QueryStub(new TimelineSnapshot(
                [todo],
                [TestData.Row("定时提醒", "2026-07-29T10:00:00+08:00")],
                0,
                0));
            var todos = new TodoServiceStub();
            var viewModel = Create(query, todos: todos);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var create = Assert.IsType<Button>(view.FindName("NewReminderButton"));
            Assert.True(create.Focus());

            Assert.True(create.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next)));
            var focused = Assert.IsAssignableFrom<DependencyObject>(
                Keyboard.FocusedElement);
            var list = focused as ListBox ?? Ancestor<ListBox>(focused);
            Assert.NotNull(list);
            var selected = Assert.IsType<TodoTimelineItemViewModel>(list.SelectedItem);
            Assert.Same(selected, viewModel.SelectedTodo);
            Assert.Null(viewModel.SelectedItem);

            var complete = Assert.Single(
                view.InputBindings.OfType<KeyBinding>(),
                binding => binding.Key == Key.Space &&
                           binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Same(viewModel.CompleteCommand, complete.Command);
            Assert.True(complete.Command.CanExecute(complete.CommandParameter));
            viewModel.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal([todo.TodoId], todos.CompletedTodoIds);
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
            var rowLists = Descendants<ListBox>(view)
                .Where(list => System.Windows.Automation.AutomationProperties
                    .GetName(list).EndsWith("提醒", StringComparison.Ordinal))
                .ToArray();
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
        ActionServiceStub? actions = null,
        TodoServiceStub? todos = null) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"),
            new ReminderServiceStub(), actions ?? new ActionServiceStub(),
            todos ?? new TodoServiceStub(), new DialogStub(), new DialogStub(),
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
            new ActionServiceStub(), new TodoServiceStub(),
            new DialogStub(), new DialogStub(),
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

    private sealed class QueryStub : ITimelineQuery
    {
        private readonly TimelineSnapshot _snapshot;

        public QueryStub(params TimelineRow[] reminders)
            : this(new TimelineSnapshot([], reminders, 0, 0))
        {
        }

        public QueryStub(TimelineSnapshot snapshot) => _snapshot = snapshot;

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult(_snapshot);
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

    private sealed class TodoServiceStub : ITodoService
    {
        public List<Guid> CompletedTodoIds { get; } = [];

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title, DateTimeOffset.UtcNow,
                draft.DueDate, draft.Importance, false, null));
        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CompleteAsync(Guid todoId, CancellationToken ct)
        {
            CompletedTodoIds.Add(todoId);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToReminderAsync(
            Guid todoId, ReminderDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class DialogStub : ITimelineDialogService, ITodoDialogService
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
        public Task EditTodoAsync(TodoItem item, CancellationToken ct) => Task.CompletedTask;
        public void OpenQuickAdd() { }
    }

    private static TodoTimelineRow TodoRow(
        string title,
        DateOnly? dueDate,
        bool isCompleted = false,
        DateTimeOffset? completedAt = null,
        ReminderImportance importance = ReminderImportance.Normal) =>
        new(
            Guid.NewGuid(), title,
            DateTimeOffset.Parse("2026-07-20T09:00:00+08:00"),
            dueDate, importance, isCompleted, completedAt);
}
