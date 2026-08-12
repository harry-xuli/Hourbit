using System.Globalization;
using Moment.App.Commands;
using Moment.App.Timeline;
using Moment.Core.Analytics;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

public sealed class TimelineViewModelTests
{
    [Fact]
    public async Task Countdown_tick_updates_loaded_rows_without_querying_database()
    {
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [],
            [TestData.Row(
                "倒计时", "2026-07-29T10:05:00+08:00") with
            { Kind = ReminderKind.Countdown }],
            0, 0));
        var vm = Create(query);
        await vm.LoadAsync();

        vm.UpdateCountdowns(DateTimeOffset.Parse("2026-07-29T10:00:01+08:00"));

        Assert.Equal("剩余 04:59", Assert.Single(vm.Items).RemainingText);
        Assert.Equal(1, query.Calls);
    }

    [Fact]
    public void Timeline_period_uses_the_active_culture_week_start()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        var period = TimelinePeriod.Create(
            new DateOnly(2026, 7, 29), TimelinePeriodKind.Week, culture);

        Assert.Equal(
            new LocalDateRange(
                new DateOnly(2026, 7, 27),
                new DateOnly(2026, 8, 2)),
            period.Range);
        Assert.Equal("2026年7月27日 – 2026年8月2日", period.Label);
    }

    [Fact]
    public void Timeline_period_month_handles_leap_day_and_moves_by_calendar_month()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var february = TimelinePeriod.Create(
            new DateOnly(2024, 2, 29), TimelinePeriodKind.Month, culture);

        var march = TimelinePeriod.Create(
            new DateOnly(2024, 3, 29), TimelinePeriodKind.Month, culture);

        Assert.Equal(
            new LocalDateRange(
                new DateOnly(2024, 2, 1),
                new DateOnly(2024, 2, 29)),
            february.Range);
        Assert.Equal("2024年2月", february.Label);
        Assert.Equal(
            new LocalDateRange(
                new DateOnly(2024, 3, 1),
                new DateOnly(2024, 3, 31)),
            march.Range);
        Assert.Equal("2024年3月", march.Label);
    }

    [Fact]
    public async Task Timeline_defaults_to_day_and_moves_each_period_by_its_calendar_unit()
    {
        var query = new RecordingPeriodQuery();
        var vm = Create(
            query,
            culture: CultureInfo.GetCultureInfo("en-US"));

        await vm.LoadAsync();
        Assert.Equal(TimelinePeriodKind.Day, vm.SelectedPeriodKind);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 7, 29)),
            query.Ranges[^1]);
        Assert.Equal("2026年7月29日 Wednesday", vm.PeriodLabel);

        await vm.PreviousPeriodCommand.ExecuteAsync(null);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 28), new DateOnly(2026, 7, 28)),
            query.Ranges[^1]);
        await vm.NextPeriodCommand.ExecuteAsync(null);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 7, 29)),
            query.Ranges[^1]);

        await vm.SelectWeekPeriodCommand.ExecuteAsync(null);
        Assert.Equal(TimelinePeriodKind.Week, vm.SelectedPeriodKind);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 26), new DateOnly(2026, 8, 1)),
            query.Ranges[^1]);
        await vm.NextPeriodCommand.ExecuteAsync(null);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8)),
            query.Ranges[^1]);

        await vm.SelectMonthPeriodCommand.ExecuteAsync(null);
        Assert.Equal(TimelinePeriodKind.Month, vm.SelectedPeriodKind);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)),
            query.Ranges[^1]);
        await vm.PreviousPeriodCommand.ExecuteAsync(null);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)),
            query.Ranges[^1]);
    }

    [Fact]
    public async Task Timeline_publishes_summary_cards_and_navigates_to_their_exact_ranges()
    {
        var opened = new List<LocalDateRange>();
        var query = new FakeTimelineQuery(new TimelineSnapshot(
            [], [], 0, 0,
            PastSevenDaysCompleted: 7,
            NextFourteenDaysPlanned: 14,
            PastSevenDaysRange: new LocalDateRange(
                new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
            NextFourteenDaysRange: new LocalDateRange(
                new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))));
        var vm = Create(query, analyticsNavigation: opened.Add);

        await vm.LoadAsync();
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);

        Assert.Equal(7, vm.PastSevenDaysCompleted);
        Assert.Equal(14, vm.NextFourteenDaysPlanned);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
            ],
            opened);
    }

    [Fact]
    public async Task Summary_card_counts_and_ranges_stay_on_the_loaded_day_until_a_new_snapshot_succeeds()
    {
        var clock = new MutableClock(
            DateTimeOffset.Parse("2026-07-29T23:59:00+08:00"));
        var query = new ClockBasedCardQuery();
        var opened = new List<LocalDateRange>();
        var vm = Create(
            query,
            clock: clock,
            analyticsNavigation: opened.Add);

        Assert.False(vm.OpenPastSevenDaysAnalyticsCommand.CanExecute(null));
        Assert.False(vm.OpenNextFourteenDaysAnalyticsCommand.CanExecute(null));
        await vm.LoadAsync();
        Assert.Equal(29, vm.PastSevenDaysCompleted);
        Assert.Equal(129, vm.NextFourteenDaysPlanned);
        Assert.True(vm.OpenPastSevenDaysAnalyticsCommand.CanExecute(null));
        Assert.True(vm.OpenNextFourteenDaysAnalyticsCommand.CanExecute(null));

        clock.Now = DateTimeOffset.Parse("2026-07-30T00:01:00+08:00");
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);

        Assert.Equal(29, vm.PastSevenDaysCompleted);
        Assert.Equal(129, vm.NextFourteenDaysPlanned);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
            ],
            opened);

        opened.Clear();
        await vm.LoadAsync();
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);

        Assert.Equal(30, vm.PastSevenDaysCompleted);
        Assert.Equal(130, vm.NextFourteenDaysPlanned);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 24), new DateOnly(2026, 7, 30)),
                new LocalDateRange(new DateOnly(2026, 7, 30), new DateOnly(2026, 8, 12))
            ],
            opened);
    }

    [Fact]
    public async Task Initial_load_failure_keeps_summary_navigation_disabled()
    {
        var vm = Create(new ThrowingTimelineQuery(
            new InvalidOperationException("统计不可用")));

        Assert.False(vm.OpenPastSevenDaysAnalyticsCommand.CanExecute(null));
        Assert.False(vm.OpenNextFourteenDaysAnalyticsCommand.CanExecute(null));

        await vm.LoadAsync();

        Assert.Equal("统计不可用", vm.ErrorMessage);
        Assert.False(vm.OpenPastSevenDaysAnalyticsCommand.CanExecute(null));
        Assert.False(vm.OpenNextFourteenDaysAnalyticsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_reload_keeps_the_last_valid_summary_counts_ranges_and_navigation()
    {
        var query = new SuccessThenFailureCardQuery();
        var opened = new List<LocalDateRange>();
        var vm = Create(query, analyticsNavigation: opened.Add);
        await vm.LoadAsync();

        await vm.LoadAsync();

        Assert.Equal("刷新统计失败", vm.ErrorMessage);
        Assert.Equal(7, vm.PastSevenDaysCompleted);
        Assert.Equal(14, vm.NextFourteenDaysPlanned);
        Assert.True(vm.OpenPastSevenDaysAnalyticsCommand.CanExecute(null));
        Assert.True(vm.OpenNextFourteenDaysAnalyticsCommand.CanExecute(null));
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
            ],
            opened);
    }

    [Fact]
    public async Task Period_switch_cancels_the_stale_day_load_and_publishes_only_the_week_snapshot()
    {
        var query = new CancelDayThenReturnWeekQuery();
        var opened = new List<LocalDateRange>();
        var vm = Create(
            query,
            culture: CultureInfo.GetCultureInfo("fr-FR"),
            analyticsNavigation: opened.Add);

        var dayLoad = vm.LoadAsync();
        await query.DayStarted.Task;
        await vm.SelectWeekPeriodCommand.ExecuteAsync(null);
        await dayLoad;

        Assert.True(query.DayCancellationObserved);
        Assert.Equal(
            new LocalDateRange(new DateOnly(2026, 7, 27), new DateOnly(2026, 8, 2)),
            query.Ranges[^1]);
        Assert.Equal("周视图", Assert.Single(vm.Items).Title);
        Assert.Equal(70, vm.PastSevenDaysCompleted);
        Assert.Equal(140, vm.NextFourteenDaysPlanned);
        Assert.Null(vm.ErrorMessage);
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
            ],
            opened);
    }

    [Fact]
    public async Task Stale_period_failure_cannot_replace_the_latest_successful_snapshot()
    {
        var query = new StaleFailureAfterWeekQuery();
        var opened = new List<LocalDateRange>();
        var vm = Create(
            query,
            culture: CultureInfo.GetCultureInfo("fr-FR"),
            analyticsNavigation: opened.Add);

        var dayLoad = vm.LoadAsync();
        await query.DayStarted.Task;
        await vm.SelectWeekPeriodCommand.ExecuteAsync(null);
        query.ReleaseDayFailure.TrySetResult();
        await dayLoad;

        Assert.Equal("最新周视图", Assert.Single(vm.Items).Title);
        Assert.Equal(80, vm.PastSevenDaysCompleted);
        Assert.Equal(150, vm.NextFourteenDaysPlanned);
        Assert.Null(vm.ErrorMessage);
        await vm.OpenPastSevenDaysAnalyticsCommand.ExecuteAsync(null);
        await vm.OpenNextFourteenDaysAnalyticsCommand.ExecuteAsync(null);
        Assert.Equal(
            [
                new LocalDateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                new LocalDateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))
            ],
            opened);
    }

    [Fact]
    public async Task Timeline_orders_by_due_time_and_exposes_text_status()
    {
        var query = new FakeTimelineQuery(
            TestData.Row("午休", "2026-07-29T12:00:00+08:00", OccurrenceState.Scheduled),
            TestData.Row("会议", "2026-07-29T10:30:00+08:00", OccurrenceState.Fired));
        var vm = Create(query);

        await vm.LoadAsync();

        Assert.Equal(["会议", "午休"], vm.Items.Select(item => item.Title));
        Assert.Equal("等待处理", vm.Items[0].StatusText);
        Assert.Equal(["已错过", "接下来", "已完成"], vm.Groups.Select(group => group.Name));
    }

    [Fact]
    public void Reminder_status_uses_persisted_delivery_state_instead_of_clock_guessing()
    {
        var now = DateTimeOffset.Parse("2026-07-29T15:56:00+08:00");
        var scheduled = new TimelineItemViewModel(
            TestData.Row("等待投递", "2026-07-29T15:50:00+08:00", OccurrenceState.Scheduled),
            now);
        var failed = new TimelineItemViewModel(
            TestData.Row("投递失败", "2026-07-29T15:51:00+08:00", OccurrenceState.DeliveryFailed),
            now);

        Assert.Equal(("接下来", "等待中"), (scheduled.GroupName, scheduled.StatusText));
        Assert.Equal(("接下来", "提醒失败"), (failed.GroupName, failed.StatusText));
    }

    [Fact]
    public async Task Timeline_separates_and_orders_todos_without_changing_the_next_reminder()
    {
        var sameDateSecond = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var sameDateFirst = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var snapshot = new TimelineSnapshot(
            [
                TodoRow("无日期", null),
                TodoRow("未来", new DateOnly(2026, 7, 31)),
                TodoRow("今天", new DateOnly(2026, 7, 29)),
                TodoRow("逾期二", new DateOnly(2026, 7, 28), id: sameDateSecond),
                TodoRow("已完成待办", null, isCompleted: true,
                    completedAt: DateTimeOffset.Parse("2026-07-29T08:00:00+08:00")),
                TodoRow("逾期一", new DateOnly(2026, 7, 28), id: sameDateFirst)
            ],
            [TestData.Row("定时会议", "2026-07-29T10:30:00+08:00")],
            TodosCompletedToday: 2,
            RemindersCompletedToday: 3);
        var vm = Create(new FakeTimelineQuery(snapshot));

        await vm.LoadAsync();

        Assert.Equal(
            ["逾期一", "逾期二", "今天", "未来", "无日期"],
            vm.PendingTodos.Select(todo => todo.Title));
        Assert.Equal("已逾期", vm.PendingTodos[0].StatusText);
        Assert.Equal("无日期", vm.PendingTodos[^1].DueDateText);
        Assert.Equal("已完成待办", Assert.Single(vm.CompletedTodos).Title);
        Assert.Equal("10:30 定时会议", vm.NextReminderText);
        Assert.Equal(5, vm.CompletedCount);
        Assert.Equal("待办：2，提醒：3", vm.CompletedTooltipText);
    }

    [Fact]
    public async Task Todo_complete_targets_the_selected_todo_and_refreshes_collections()
    {
        var todo = TodoRow("提交报表", new DateOnly(2026, 7, 29));
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        var todos = new RecordingTodoService
        {
            AfterComplete = () => query.Snapshot = new TimelineSnapshot(
                [todo with
                {
                    IsCompleted = true,
                    CompletedAt = DateTimeOffset.Parse("2026-07-29T09:05:00+08:00")
                }], [], 1, 0)
        };
        var vm = Create(query, todos: todos);
        await vm.LoadAsync();

        await vm.CompleteCommand.ExecuteAsync(null);

        Assert.Equal([todo.TodoId], todos.Completed);
        Assert.Empty(vm.PendingTodos);
        Assert.Equal("提交报表", Assert.Single(vm.CompletedTodos).Title);
    }

    [Fact]
    public async Task Todo_delete_targets_the_selected_todo_and_refreshes_collections()
    {
        var todo = TodoRow("清理记录", null);
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        var todos = new RecordingTodoService
        {
            AfterDelete = () => query.Snapshot = new TimelineSnapshot([], [], 0, 0)
        };
        var vm = Create(query, todos: todos);
        await vm.LoadAsync();

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal([todo.TodoId], todos.Deleted);
        Assert.Empty(vm.PendingTodos);
    }

    [Fact]
    public async Task Copy_command_routes_to_the_selected_item_type()
    {
        var todo = TodoRow("复制待办", null);
        var reminder = TestData.Row(
            "复制提醒", "2026-07-29T10:30:00+08:00");
        var dialogs = new Dialogs();
        var vm = Create(
            new FakeTimelineQuery(new TimelineSnapshot([todo], [reminder], 0, 0)),
            dialogs: dialogs);
        await vm.LoadAsync();

        Assert.True(vm.CopyCommand.CanExecute(null));
        await vm.CopyCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();
        await vm.CopyCommand.ExecuteAsync(null);
        vm.SelectedItem = null;

        Assert.Equal([todo.TodoId], dialogs.CopiedTodos);
        Assert.Equal([reminder.OccurrenceId], dialogs.CopiedReminders);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Todo_edit_does_not_duplicate_a_dialog_owned_conversion_refresh()
    {
        var todo = TodoRow("项目复盘", new DateOnly(2026, 7, 29));
        var converted = TestData.Row(
            "项目复盘", "2026-07-29T14:30:00+08:00");
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        TimelineViewModel? vm = null;
        var dialogs = new Dialogs();
        dialogs.DuringTodoEdit = async () =>
        {
            query.Snapshot = new TimelineSnapshot([], [converted], 0, 0);
            await vm!.LoadAsync();
        };
        vm = Create(query, dialogs: dialogs);
        await vm.LoadAsync();

        await vm.EditCommand.ExecuteAsync(null);

        var editedTodo = Assert.Single(dialogs.EditedTodos);
        Assert.Equal(todo.TodoId, editedTodo.Id);
        Assert.Equal("项目复盘", editedTodo.Title);
        Assert.Equal(new DateOnly(2026, 7, 29), editedTodo.DueDate);
        Assert.Equal(ReminderImportance.Normal, editedTodo.Importance);
        Assert.False(editedTodo.IsCompleted);
        Assert.Empty(vm.PendingTodos);
        Assert.Equal("项目复盘", Assert.Single(vm.Items).Title);
        Assert.Equal(2, query.Calls);
    }

    [Fact]
    public async Task Todo_edit_cancel_does_not_refresh_the_timeline()
    {
        var todo = TodoRow("取消编辑", null);
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        var dialogs = new Dialogs();
        var vm = Create(query, dialogs: dialogs);
        await vm.LoadAsync();

        await vm.EditCommand.ExecuteAsync(null);

        Assert.Equal(1, query.Calls);
        Assert.Equal("取消编辑", Assert.Single(vm.PendingTodos).Title);
    }

    [Fact]
    public async Task Todo_edit_refreshes_once_when_the_dialog_requests_caller_ownership()
    {
        var todo = TodoRow("测试 seam", null);
        var converted = TestData.Row(
            "测试 seam", "2026-07-29T15:00:00+08:00");
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        var dialogs = new Dialogs
        {
            TodoEditResult = new TodoDialogResult(RequiresCallerRefresh: true),
            DuringTodoEdit = () =>
            {
                query.Snapshot = new TimelineSnapshot([], [converted], 0, 0);
                return Task.CompletedTask;
            }
        };
        var vm = Create(query, dialogs: dialogs);
        await vm.LoadAsync();

        await vm.EditCommand.ExecuteAsync(null);

        Assert.Equal(2, query.Calls);
        Assert.Empty(vm.PendingTodos);
        Assert.Equal("测试 seam", Assert.Single(vm.Items).Title);
    }

    [Fact]
    public async Task Second_load_cancels_stale_query_and_publishes_only_latest_rows()
    {
        var query = new CancelThenReturnQuery(TestData.Row(
            "最新", "2026-07-29T11:00:00+08:00"));
        var vm = Create(query);

        var first = vm.LoadAsync();
        await query.FirstStarted.Task;
        var second = vm.LoadAsync();
        await Task.WhenAll(first, second);

        Assert.True(query.FirstCancellationObserved);
        Assert.Equal("最新", Assert.Single(vm.Items).Title);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Load_failure_is_observable_and_does_not_escape_the_command()
    {
        var vm = Create(new ThrowingTimelineQuery(new InvalidOperationException("数据库不可用")));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("数据库不可用", vm.ErrorMessage);
        Assert.False(vm.LoadCommand.IsRunning);
    }

    [Fact]
    public async Task Complete_command_rejects_reentrancy_while_the_first_action_is_running()
    {
        var action = new BlockingActionService();
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "会议", "2026-07-29T10:30:00+08:00")), actions: action);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        var first = vm.CompleteCommand.ExecuteAsync(null);
        await action.Entered.Task;
        var second = vm.CompleteCommand.ExecuteAsync(null);
        Assert.True(vm.CompleteCommand.IsRunning);
        action.Release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, action.CompleteCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Recurring_edit_cancelled_at_scope_does_not_call_reminder_service()
    {
        var service = new RecordingReminderService();
        var dialogs = new Dialogs { EditScope = null };
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "午休", "2026-07-29T12:00:00+08:00",
            recurrenceText: "每天")), service, dialogs);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.EditCommand.ExecuteAsync(null);

        Assert.Empty(service.Edited);
        Assert.Equal(0, dialogs.EditFormCalls);
    }

    [Fact]
    public async Task Edited_values_reach_reminder_service_without_being_replaced_by_original_row()
    {
        var service = new RecordingReminderService();
        var editedDraft = new ReminderDraft(
            "项目复盘", DateTimeOffset.Parse("2026-08-03T14:45:00+08:00"),
            ReminderKind.Plan, ReminderImportance.Important,
            RecurrenceRule.Daily(new TimeOnly(14, 45)));
        var dialogs = new Dialogs { EditedDraft = editedDraft };
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "会议", "2026-07-29T10:30:00+08:00")), service, dialogs);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.EditCommand.ExecuteAsync(null);

        var edit = Assert.Single(service.Edited);
        Assert.Equal(editedDraft, edit.Draft);
        Assert.Equal(SeriesScope.OccurrenceOnly, edit.Scope);
    }

    [Fact]
    public async Task Recurring_delete_cancelled_at_scope_does_not_call_reminder_service()
    {
        var service = new RecordingReminderService();
        var dialogs = new Dialogs { DeleteScope = null };
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "午休", "2026-07-29T12:00:00+08:00",
            recurrenceText: "每天")), service, dialogs);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(service.Deleted);
    }

    [Fact]
    public async Task Non_recurring_delete_cancelled_at_confirmation_does_not_call_reminder_service()
    {
        var service = new RecordingReminderService();
        var dialogs = new Dialogs { ConfirmDelete = false };
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "会议", "2026-07-29T10:30:00+08:00")), service, dialogs);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.DeleteConfirmationCalls);
        Assert.Empty(service.Deleted);
    }

    [Fact]
    public async Task Non_recurring_delete_confirmed_calls_reminder_service_once()
    {
        var service = new RecordingReminderService();
        var dialogs = new Dialogs { ConfirmDelete = true };
        var vm = Create(new FakeTimelineQuery(TestData.Row(
            "会议", "2026-07-29T10:30:00+08:00")), service, dialogs);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.DeleteConfirmationCalls);
        Assert.Single(service.Deleted);
    }

    [Fact]
    public async Task Quick_Add_window_failure_is_exposed_instead_of_escaping_the_command()
    {
        var dialogs = new Dialogs { QuickAddFailure = new InvalidOperationException("窗口不可用") };
        var vm = Create(new FakeTimelineQuery(), dialogs: dialogs);

        await vm.OpenQuickAddCommand.ExecuteAsync(null);

        Assert.Equal("窗口不可用", vm.ErrorMessage);
    }

    private static TimelineViewModel Create(
        ITimelineQuery query,
        IReminderService? service = null,
        ITimelineDialogService? dialogs = null,
        IReminderActionService? actions = null,
        ITodoService? todos = null,
        CultureInfo? culture = null,
        Action<LocalDateRange>? analyticsNavigation = null,
        IClock? clock = null)
    {
        var reminderDialogs = dialogs ?? new Dialogs();
        var todoDialogs = reminderDialogs as ITodoDialogService ?? new Dialogs();
        return new TimelineViewModel(
            query, clock ?? new FakeClock("2026-07-29T09:00:00+08:00"),
            service ?? new RecordingReminderService(),
            actions ?? new BlockingActionService(completesImmediately: true),
            todos ?? new RecordingTodoService(),
            reminderDialogs,
            todoDialogs,
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-vm", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            culture,
            analyticsNavigation);
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public Task DelayUntilAsync(
            DateTimeOffset dueAt,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ClockBasedCardQuery : ITimelineQuery
    {
        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CancellationToken ct)
        {
            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(now, zone).DateTime);
            return Task.FromResult(new TimelineSnapshot(
                [], [], 0, 0,
                PastSevenDaysCompleted: today.Day,
                NextFourteenDaysPlanned: 100 + today.Day,
                PastSevenDaysRange: new LocalDateRange(today.AddDays(-6), today),
                NextFourteenDaysRange: new LocalDateRange(today, today.AddDays(13))));
        }
    }

    private sealed class SuccessThenFailureCardQuery : ITimelineQuery
    {
        private int _calls;

        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                return Task.FromException<TimelineSnapshot>(
                    new InvalidOperationException("刷新统计失败"));
            }

            return Task.FromResult(new TimelineSnapshot(
                [], [], 0, 0,
                PastSevenDaysCompleted: 7,
                NextFourteenDaysPlanned: 14,
                PastSevenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                NextFourteenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11))));
        }
    }

    private sealed class RecordingPeriodQuery : ITimelineQuery
    {
        public List<LocalDateRange> Ranges { get; } = [];

        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CancellationToken ct)
        {
            Ranges.Add(range);
            return Task.FromResult(new TimelineSnapshot([], [], 0, 0));
        }
    }

    private sealed class CancelDayThenReturnWeekQuery : ITimelineQuery
    {
        public TaskCompletionSource DayStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<LocalDateRange> Ranges { get; } = [];
        public bool DayCancellationObserved { get; private set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CancellationToken ct)
        {
            Ranges.Add(range);
            if (Ranges.Count == 1)
            {
                DayStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    DayCancellationObserved = true;
                    return new TimelineSnapshot(
                        [], [TestData.Row("旧日视图", "2026-07-29T09:30:00+08:00")], 0, 0,
                        PastSevenDaysCompleted: 99,
                        NextFourteenDaysPlanned: 199,
                        PastSevenDaysRange: new LocalDateRange(
                            new DateOnly(2026, 7, 22), new DateOnly(2026, 7, 28)),
                        NextFourteenDaysRange: new LocalDateRange(
                            new DateOnly(2026, 7, 28), new DateOnly(2026, 8, 10)));
                }
            }

            return new TimelineSnapshot(
                [], [TestData.Row("周视图", "2026-07-29T10:00:00+08:00")], 0, 0,
                PastSevenDaysCompleted: 70,
                NextFourteenDaysPlanned: 140,
                PastSevenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                NextFourteenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11)));
        }
    }

    private sealed class StaleFailureAfterWeekQuery : ITimelineQuery
    {
        private int _calls;
        public TaskCompletionSource DayStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDayFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                DayStarted.TrySetResult();
                await ReleaseDayFailure.Task;
                throw new InvalidOperationException("旧查询失败");
            }

            return new TimelineSnapshot(
                [], [TestData.Row("最新周视图", "2026-07-29T10:00:00+08:00")], 0, 0,
                PastSevenDaysCompleted: 80,
                NextFourteenDaysPlanned: 150,
                PastSevenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 29)),
                NextFourteenDaysRange: new LocalDateRange(
                    new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 11)));
        }
    }

    private sealed class FakeTimelineQuery : ITimelineQuery
    {
        private readonly TimelineSnapshot _snapshot;

        public FakeTimelineQuery(params TimelineRow[] reminders)
            : this(new TimelineSnapshot([], reminders, 0, 0))
        {
        }

        public FakeTimelineQuery(TimelineSnapshot snapshot) => _snapshot = snapshot;

        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult(_snapshot);
    }

    private sealed class MutableTimelineQuery(TimelineSnapshot snapshot) : ITimelineQuery
    {
        public TimelineSnapshot Snapshot { get; set; } = snapshot;
        public int Calls { get; private set; }

        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class ThrowingTimelineQuery(Exception exception) : ITimelineQuery
    {
        public Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromException<TimelineSnapshot>(exception);
    }

    private sealed class CancelThenReturnQuery(TimelineRow row) : ITimelineQuery
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstCancellationObserved { get; private set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FirstCancellationObserved = true;
                    throw;
                }
            }
            return new TimelineSnapshot([], [row], 0, 0);
        }
    }

    private sealed class BlockingActionService(bool completesImmediately = false) : IReminderActionService
    {
        private readonly bool _completesImmediately = completesImmediately;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompleteCalls { get; private set; }

        public async Task CompleteAsync(Guid occurrenceId, CancellationToken ct)
        {
            CompleteCalls++;
            Entered.TrySetResult();
            if (!_completesImmediately)
                await Release.Task.WaitAsync(ct);
        }

        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) => Task.CompletedTask;
        public Task<ReminderOccurrence> SnoozeAsync(Guid occurrenceId, TimeSpan delay, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    private sealed class RecordingReminderService : IReminderService
    {
        public List<(Guid Id, ReminderDraft Draft, SeriesScope Scope)> Edited { get; } = [];
        public List<(Guid Id, SeriesScope Scope)> Deleted { get; } = [];
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct)
        {
            Edited.Add((occurrenceId, draft, scope));
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
        {
            Deleted.Add((occurrenceId, scope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTodoService : ITodoService
    {
        public List<Guid> Completed { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Action? AfterComplete { get; init; }
        public Action? AfterDelete { get; init; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title,
                DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"),
                draft.DueDate, draft.Importance, false, null));
        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CompleteAsync(Guid todoId, CancellationToken ct)
        {
            Completed.Add(todoId);
            AfterComplete?.Invoke();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid todoId, CancellationToken ct)
        {
            Deleted.Add(todoId);
            AfterDelete?.Invoke();
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

    private sealed class Dialogs : ITimelineDialogService, ITodoDialogService
    {
        public SeriesScope? EditScope { get; set; } = SeriesScope.OccurrenceOnly;
        public SeriesScope? DeleteScope { get; set; } = SeriesScope.OccurrenceOnly;
        public bool ConfirmDelete { get; set; } = true;
        public ReminderDraft? EditedDraft { get; set; }
        public int DeleteConfirmationCalls { get; private set; }
        public int EditFormCalls { get; private set; }
        public Exception? QuickAddFailure { get; set; }
        public Func<Task>? DuringTodoEdit { get; set; }
        public TodoDialogResult TodoEditResult { get; set; }
        public List<TodoItem> EditedTodos { get; } = [];
        public List<Guid> CopiedReminders { get; } = [];
        public List<Guid> CopiedTodos { get; } = [];
        public Task<SeriesScope?> SelectEditScopeAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(EditScope);
        public Task<SeriesScope?> SelectDeleteScopeAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(DeleteScope);
        public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            DeleteConfirmationCalls++;
            return Task.FromResult(ConfirmDelete);
        }
        public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            EditFormCalls++;
            return Task.FromResult<ReminderDraft?>(EditedDraft ?? new(
                item.Title, item.DueAt, item.Kind, item.Importance, null));
        }
        public async Task<TodoDialogResult> EditTodoAsync(
            TodoItem item,
            CancellationToken ct)
        {
            EditedTodos.Add(item);
            if (DuringTodoEdit is not null)
                await DuringTodoEdit();
            return TodoEditResult;
        }
        public Task CopyReminderAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            CopiedReminders.Add(item.OccurrenceId);
            return Task.CompletedTask;
        }
        public Task CopyTodoAsync(TodoTimelineItemViewModel item, CancellationToken ct)
        {
            CopiedTodos.Add(item.TodoId);
            return Task.CompletedTask;
        }
        public void OpenQuickAdd()
        {
            if (QuickAddFailure is not null)
                throw QuickAddFailure;
        }
    }

    private static TodoTimelineRow TodoRow(
        string title,
        DateOnly? dueDate,
        bool isCompleted = false,
        DateTimeOffset? completedAt = null,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            title,
            DateTimeOffset.Parse("2026-07-20T09:00:00+08:00"),
            dueDate,
            ReminderImportance.Normal,
            isCompleted,
            completedAt);
}
