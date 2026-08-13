using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using Moment.App.Timeline;
using Moment.App.Search;
using Moment.App.Styles;
using Moment.Core.Abstractions;
using Moment.Core.Analytics;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

public sealed class TimelineViewTests
{
    [Fact]
    public Task Search_controls_are_keyboard_accessible_and_ctrl_f_focuses_the_query_box() =>
        WpfTestHost.RunAsync(async () =>
        {
            var search = new SearchViewModel(new SearchQueryStub(), _ => Task.CompletedTask);
            var viewModel = Create(TwoGroupQuery(), search: search);
            await viewModel.LoadAsync();
            var view = Show(viewModel);
            var query = Assert.IsType<TextBox>(view.FindName("GlobalSearchBox"));
            var button = Assert.IsType<Button>(view.FindName("GlobalSearchButton"));
            var results = Assert.IsType<ListBox>(view.FindName("GlobalSearchResults"));

            Assert.Equal("全局搜索", PeerName(query));
            Assert.Equal("搜索", PeerName(button));
            Assert.Equal("搜索结果", PeerName(results));
            Assert.True(RaiseKey(view, Key.F, ModifierKeys.Control).Handled);
            Assert.Same(query, Keyboard.FocusedElement);
        });
    private static readonly string[] ExpectedGroups = ["已错过", "接下来", "已完成"];

    [Fact]
    public Task Period_selectors_and_summary_cards_are_keyboard_accessible_above_the_sections() =>
        WpfTestHost.RunAsync(async () =>
        {
            var opened = new List<LocalDateRange>();
            var query = new QueryStub(new TimelineSnapshot(
                [], [], 0, 0,
                PastSevenDaysCompleted: 3,
                NextFourteenDaysPlanned: 5,
                PastSevenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                NextFourteenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))));
            var viewModel = Create(
                query,
                analyticsNavigation: opened.Add,
                culture: CultureInfo.GetCultureInfo("zh-CN"));
            await viewModel.LoadAsync();
            var view = Show(viewModel);
            var day = Assert.IsType<RadioButton>(view.FindName("DayPeriodButton"));
            var week = Assert.IsType<RadioButton>(view.FindName("WeekPeriodButton"));
            var month = Assert.IsType<RadioButton>(view.FindName("MonthPeriodButton"));
            var previous = Assert.IsType<Button>(view.FindName("PreviousPeriodButton"));
            var next = Assert.IsType<Button>(view.FindName("NextPeriodButton"));
            var chooseDate = Assert.IsType<Button>(view.FindName("ChooseDateButton"));
            var past = Assert.IsType<Button>(view.FindName("PastSevenDaysCard"));
            var future = Assert.IsType<Button>(view.FindName("NextFourteenDaysCard"));
            var periodLabel = Assert.IsType<TextBlock>(view.FindName("PeriodLabel"));
            var todoSection = Assert.IsType<Grid>(view.FindName("TodoSection"));

            Assert.Equal(["日", "周", "月"],
                new[] { day, week, month }.Select(button => button.Content));
            Assert.DoesNotContain(
                Descendants<RadioButton>(view),
                button => Equals(button.Content, "年"));
            Assert.True(day.IsChecked);
            Assert.Equal("按日查看", PeerName(day));
            Assert.Equal("按周查看", PeerName(week));
            Assert.Equal("按月查看", PeerName(month));
            Assert.All(
                new[] { day, week, month },
                button => Assert.IsAssignableFrom<Geometry>(button.Tag));
            Assert.Equal("上一时段", PeerName(previous));
            Assert.Equal("下一时段", PeerName(next));
            Assert.Equal("选择日期", PeerName(chooseDate));
            Assert.Equal("过去 7 天已完成：3，打开分析", PeerName(past));
            Assert.Equal("未来 14 天计划：5，打开分析", PeerName(future));
            Assert.Equal("2026年7月29日 星期三", periodLabel.Text);
            Assert.All(
                new Control[] { day, week, month, previous, next, chooseDate, past, future },
                control => Assert.True(Peer(control).IsKeyboardFocusable()));
            Assert.True(past.TranslatePoint(new Point(), view).Y <
                        todoSection.TranslatePoint(new Point(), view).Y);
            Assert.True(future.TranslatePoint(new Point(), view).Y <
                        todoSection.TranslatePoint(new Point(), view).Y);

            Assert.True(week.Focus());
            RaiseKeyStroke(week, Key.Space);
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.Equal(TimelinePeriodKind.Week, viewModel.SelectedPeriodKind);
            Assert.True(week.IsChecked);
            Assert.True(previous.Focus());
            RaiseKeyStroke(previous, Key.Space);
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.Equal("2026年7月20日 – 2026年7月26日", periodLabel.Text);
            Assert.True(next.Focus());
            RaiseKeyStroke(next, Key.Space);
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.Equal("2026年7月27日 – 2026年8月2日", periodLabel.Text);
            Assert.IsAssignableFrom<IInvokeProvider>(
                Peer(past).GetPattern(PatternInterface.Invoke)).Invoke();
            Assert.IsAssignableFrom<IInvokeProvider>(
                Peer(future).GetPattern(PatternInterface.Invoke)).Invoke();
            await System.Windows.Threading.Dispatcher.Yield();

            Assert.Equal(
                [
                    new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                    new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
                ],
                opened);
        });

    [Fact]
    public Task New_reminder_vector_icon_and_label_are_vertically_centered() =>
        WpfTestHost.RunAsync(() =>
        {
            var viewModel = Create(TwoGroupQuery());
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var button = Assert.IsType<Button>(view.FindName("NewReminderButton"));
            var help = Assert.IsType<Button>(view.FindName("HelpButton"));
            var chinese = Assert.IsType<Button>(view.FindName("ChineseLanguageButton"));
            var english = Assert.IsType<Button>(view.FindName("EnglishLanguageButton"));
            var reports = Assert.IsType<Button>(view.FindName("ReportsButton"));
            var content = Assert.IsType<StackPanel>(button.Content);
            var icon = Assert.IsType<Viewbox>(content.Children[0]);
            var label = Assert.IsType<TextBlock>(content.Children[1]);

            Assert.Equal(16d, icon.ActualWidth);
            Assert.Equal(16d, icon.ActualHeight);
            Assert.Equal("新建提醒", label.Text);
            Assert.Equal(10d, icon.Margin.Right);
            Assert.Equal("?", help.Content);
            Assert.Equal("使用说明", PeerName(help));
            Assert.Equal("使用说明", help.ToolTip);
            Assert.True(Peer(help).IsKeyboardFocusable());
            Assert.Equal("中", chinese.Content);
            Assert.Equal("EN", english.Content);
            Assert.Equal("打开报告", PeerName(reports));
            Assert.All(new[] { chinese, english, reports },
                button => Assert.True(Peer(button).IsKeyboardFocusable()));

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
    public Task Split_timeline_renders_accessible_reminders_left_of_pending_todos_only() =>
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
            Assert.Null(view.FindName("CompletedTodosExpander"));
            var completedSummary = Assert.IsType<StackPanel>(
                view.FindName("CompletedSummary"));
            var pending = Assert.IsType<ListBox>(view.FindName("PendingTodoList"));
            var todoRow = Assert.IsType<ListBoxItem>(
                pending.ItemContainerGenerator.ContainerFromIndex(0));
            var todoTitle = Assert.Single(
                Descendants<TextBlock>(todoRow), text => text.Name == "TodoTitle");

            Assert.Equal("待办事项", todoHeader.Text);
            Assert.Equal("待办事项", PeerName(todoHeader));
            Assert.Equal("待办事项列表", PeerName(pending));
            Assert.Equal("定时提醒", reminderHeader.Text);
            Assert.Equal("定时提醒", PeerName(reminderHeader));
            Assert.Equal(
                "待办：逾期任务，2026-07-28，重要，已逾期",
                PeerName(todoRow));
            Assert.True(reminderHeader.TranslatePoint(new Point(), view).X <
                        todoHeader.TranslatePoint(new Point(), view).X);
            Assert.Equal("待办：1，提醒：0", completedSummary.ToolTip);
            Assert.Contains("!", VisibleText(view));
            Assert.Contains("重要", VisibleText(view));
            Assert.DoesNotContain("无日期", VisibleText(view));
            Assert.DoesNotContain(
                Descendants<TextBlock>(todoRow), text => text.Name == "TodoDueDate");
            Assert.True(todoTitle.ActualWidth >= 40d);
            Assert.DoesNotContain("完成任务", VisibleText(view));
        });

    [Fact]
    public Task Routed_keyboard_shortcuts_target_the_focused_todo_row() =>
        WpfTestHost.RunAsync(async () =>
        {
            var todo = TodoRow("键盘待办", new DateOnly(2026, 7, 29));
            var query = new QueryStub(new TimelineSnapshot(
                [todo],
                [TestData.Row("定时提醒", "2026-07-29T10:00:00+08:00")],
                0,
                0));
            var todos = new TodoServiceStub();
            var actions = new ActionServiceStub();
            var dialogs = new DialogStub();
            var reminders = new ReminderServiceStub();
            var viewModel = Create(
                query, actions, todos, reminders, dialogs);
            await viewModel.LoadAsync();
            var view = Show(viewModel);
            var list = Assert.IsType<ListBox>(view.FindName("PendingTodoList"));
            var row = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.Focus());
            Assert.Same(row, Keyboard.FocusedElement);
            viewModel.SelectedItem = viewModel.Items[0];
            Assert.Same(row, Keyboard.FocusedElement);
            Assert.NotNull(viewModel.SelectedItem);
            Assert.Null(viewModel.SelectedTodo);

            Assert.True(RaiseKey(row, Key.Enter).Handled);
            Assert.Equal([todo.TodoId], dialogs.EditedTodoIds);
            Assert.Empty(dialogs.EditedReminderIds);

            Assert.True(RaiseKey(row, Key.D, ModifierKeys.Control).Handled);
            Assert.Equal([todo.TodoId], dialogs.CopiedTodoIds);
            Assert.Empty(dialogs.CopiedReminderIds);

            Assert.True(RaiseKey(row, Key.Delete).Handled);
            Assert.Equal([todo.TodoId], todos.DeletedTodoIds);
            Assert.Empty(reminders.DeletedOccurrenceIds);

            view.UpdateLayout();
            row = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.Focus());
            Assert.True(RaiseKey(
                row, Key.Space,
                ModifierKeys.Control | ModifierKeys.Shift).Handled);
            Assert.Equal([todo.TodoId], todos.CompletedTodoIds);
            Assert.Empty(actions.CompletedOccurrenceIds);

            view.UpdateLayout();
            row = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.Focus());
            Assert.True(RaiseKey(
                row, Key.N, ModifierKeys.Control).Handled);
            Assert.Equal(1, dialogs.QuickAddCalls);
            await System.Windows.Threading.Dispatcher.Yield();
        });

    [Fact]
    public Task F5_refreshes_from_non_row_focus_without_changing_selection() =>
        WpfTestHost.RunAsync(async () =>
        {
            var query = TwoGroupQuery();
            var viewModel = Create(query);
            await viewModel.LoadAsync();
            var view = Show(viewModel);
            var help = Assert.IsType<Button>(view.FindName("HelpButton"));
            Assert.True(help.Focus());

            Assert.True(RaiseKey(help, Key.F5).Handled);
            await System.Windows.Threading.Dispatcher.Yield();

            Assert.Equal(2, query.Calls);
        });

    [Fact]
    public Task Routed_keyboard_shortcuts_switch_to_the_focused_reminder_type() =>
        WpfTestHost.RunAsync(async () =>
        {
            var todo = TodoRow("键盘待办", new DateOnly(2026, 7, 29));
            var reminder = TestData.Row(
                "键盘提醒", "2026-07-29T10:00:00+08:00");
            var query = new QueryStub(new TimelineSnapshot(
                [todo], [reminder], 0, 0));
            var todos = new TodoServiceStub();
            var actions = new ActionServiceStub();
            var dialogs = new DialogStub();
            var reminders = new ReminderServiceStub();
            var viewModel = Create(
                query, actions, todos, reminders, dialogs);
            await viewModel.LoadAsync();
            var view = Show(viewModel);
            var list = Descendants<ListBox>(view).Single(
                candidate => candidate.Items.Contains(viewModel.Items[0]));
            list.SelectedItem = viewModel.Items[0];
            var row = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.Focus());
            Assert.Same(row, Keyboard.FocusedElement);
            viewModel.SelectedTodo = viewModel.PendingTodos[0];
            Assert.Same(row, Keyboard.FocusedElement);
            Assert.NotNull(viewModel.SelectedTodo);
            Assert.Null(viewModel.SelectedItem);

            Assert.True(RaiseKey(row, Key.Enter).Handled);
            Assert.Equal([reminder.OccurrenceId], dialogs.EditedReminderIds);
            Assert.Empty(dialogs.EditedTodoIds);

            Assert.True(RaiseKey(row, Key.D, ModifierKeys.Control).Handled);
            Assert.Equal([reminder.OccurrenceId], dialogs.CopiedReminderIds);
            Assert.Empty(dialogs.CopiedTodoIds);

            Assert.True(RaiseKey(row, Key.Delete).Handled);
            Assert.Equal([reminder.OccurrenceId], reminders.DeletedOccurrenceIds);
            Assert.Empty(todos.DeletedTodoIds);

            view.UpdateLayout();
            list = Descendants<ListBox>(view).Single(
                candidate => candidate.Items.Contains(viewModel.Items[0]));
            list.SelectedItem = viewModel.Items[0];
            row = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.Focus());
            Assert.True(RaiseKey(
                row, Key.Space,
                ModifierKeys.Control | ModifierKeys.Shift).Handled);
            Assert.Equal([reminder.OccurrenceId], actions.CompletedOccurrenceIds);
            Assert.Empty(todos.CompletedTodoIds);
            await System.Windows.Threading.Dispatcher.Yield();
        });

    [Fact]
    public Task Item_shortcuts_ignore_button_and_section_focus_but_ctrl_n_remains_global() =>
        WpfTestHost.RunAsync(() =>
        {
            var todo = TodoRow("旧选择待办", new DateOnly(2026, 7, 29));
            var reminder = TestData.Row(
                "旧选择提醒", "2026-07-29T10:00:00+08:00");
            var query = new QueryStub(new TimelineSnapshot(
                [todo], [reminder], 0, 0));
            var todos = new TodoServiceStub();
            var actions = new ActionServiceStub();
            var dialogs = new DialogStub();
            var reminders = new ReminderServiceStub();
            var viewModel = Create(
                query, actions, todos, reminders, dialogs);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var view = Show(viewModel);
            var button = Assert.IsType<Button>(
                view.FindName("NewReminderButton"));
            var copyButton = Assert.IsType<Button>(
                view.FindName("CopyItemButton"));
            var section = Assert.IsType<TextBlock>(
                view.FindName("TodoSectionHeader"));
            section.Focusable = true;

            Assert.Equal("复制（Ctrl+D）", copyButton.Content);
            Assert.Equal("复制当前事项并创建新记录", copyButton.ToolTip);
            Assert.Equal("复制当前事项", PeerName(copyButton));
            Assert.True(Peer(copyButton).IsKeyboardFocusable());

            foreach (var target in new UIElement[] { button, section })
            {
                Assert.True(target.Focus());
                Assert.Same(target, Keyboard.FocusedElement);
                Assert.False(RaiseKey(target, Key.Enter).Handled);
                Assert.False(RaiseKey(target, Key.Delete).Handled);
                Assert.False(RaiseKey(target, Key.D, ModifierKeys.Control).Handled);
                Assert.False(RaiseKey(
                    target,
                    Key.Space,
                    ModifierKeys.Control | ModifierKeys.Shift).Handled);
                Assert.True(RaiseKey(
                    target, Key.N, ModifierKeys.Control).Handled);
            }

            Assert.Empty(dialogs.EditedTodoIds);
            Assert.Empty(dialogs.EditedReminderIds);
            Assert.Empty(todos.DeletedTodoIds);
            Assert.Empty(reminders.DeletedOccurrenceIds);
            Assert.Empty(todos.CompletedTodoIds);
            Assert.Empty(actions.CompletedOccurrenceIds);
            Assert.Empty(dialogs.CopiedTodoIds);
            Assert.Empty(dialogs.CopiedReminderIds);
            Assert.Equal(2, dialogs.QuickAddCalls);
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

    private static string PeerName(FrameworkElement element)
    {
        return Peer(element).GetName();
    }

    private static AutomationPeer Peer(FrameworkElement element) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException("The control must have an automation peer.");

    private static KeyEventArgs RaiseKey(
        UIElement target,
        Key key,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        var window = Window.GetWindow(target)
            ?? throw new InvalidOperationException("The target must belong to a shown window.");
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("The shown window must have an HWND source.");
        var keyboard = new TestKeyboardDevice(modifiers);
        Assert.Equal(modifiers, keyboard.Modifiers);
        var eventArgs = new KeyEventArgs(
            keyboard,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(eventArgs);
        return eventArgs;
    }

    private static void RaiseKeyStroke(UIElement target, Key key)
    {
        var window = Window.GetWindow(target)
            ?? throw new InvalidOperationException("The target must belong to a shown window.");
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("The shown window must have an HWND source.");
        var keyboard = new TestKeyboardDevice(ModifierKeys.None);
        target.RaiseEvent(new KeyEventArgs(
            keyboard, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        target.RaiseEvent(new KeyEventArgs(
            keyboard, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        });
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
        TodoServiceStub? todos = null,
        ReminderServiceStub? reminders = null,
        DialogStub? dialogs = null,
        Action<LocalDateRange>? analyticsNavigation = null,
        CultureInfo? culture = null,
        SearchViewModel? search = null) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"),
            reminders ?? new ReminderServiceStub(), actions ?? new ActionServiceStub(),
            todos ?? new TodoServiceStub(), dialogs ?? new DialogStub(),
            dialogs ?? new DialogStub(),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-view", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            culture,
            analyticsNavigation,
            search: search);

    private sealed class SearchQueryStub : Moment.Core.Search.IItemSearchQuery
    {
        public Task<IReadOnlyList<Moment.Core.Search.ItemSearchResult>> SearchAsync(
            Moment.Core.Search.ItemSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Moment.Core.Search.ItemSearchResult>>([]);
    }

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

        public int Calls { get; private set; }

        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_snapshot);
        }
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
        public List<Guid> DeletedOccurrenceIds { get; } = [];
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
        {
            DeletedOccurrenceIds.Add(occurrenceId);
            return Task.CompletedTask;
        }
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
        public List<Guid> DeletedTodoIds { get; } = [];

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
        public Task DeleteAsync(Guid todoId, CancellationToken ct)
        {
            DeletedTodoIds.Add(todoId);
            return Task.CompletedTask;
        }
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
        public List<Guid> EditedReminderIds { get; } = [];
        public List<Guid> EditedTodoIds { get; } = [];
        public List<Guid> CopiedReminderIds { get; } = [];
        public List<Guid> CopiedTodoIds { get; } = [];
        public int QuickAddCalls { get; private set; }
        public Task<SeriesScope?> SelectEditScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<SeriesScope?> SelectDeleteScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            EditedReminderIds.Add(item.OccurrenceId);
            return Task.FromResult<ReminderDraft?>(null);
        }
        public Task<TodoDialogResult> EditTodoAsync(
            TodoItem item,
            CancellationToken ct)
        {
            EditedTodoIds.Add(item.Id);
            return Task.FromResult(new TodoDialogResult(false));
        }
        public Task CopyReminderAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            CopiedReminderIds.Add(item.OccurrenceId);
            return Task.CompletedTask;
        }
        public Task CopyTodoAsync(TodoTimelineItemViewModel item, CancellationToken ct)
        {
            CopiedTodoIds.Add(item.TodoId);
            return Task.CompletedTask;
        }
        public void OpenQuickAdd() => QuickAddCalls++;
    }

    private sealed class TestKeyboardDevice(ModifierKeys modifiers)
        : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            var isDown = key switch
            {
                Key.LeftCtrl or Key.RightCtrl =>
                    modifiers.HasFlag(ModifierKeys.Control),
                Key.LeftShift or Key.RightShift =>
                    modifiers.HasFlag(ModifierKeys.Shift),
                Key.LeftAlt or Key.RightAlt =>
                    modifiers.HasFlag(ModifierKeys.Alt),
                _ => false
            };
            return isDown ? KeyStates.Down : KeyStates.None;
        }
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
