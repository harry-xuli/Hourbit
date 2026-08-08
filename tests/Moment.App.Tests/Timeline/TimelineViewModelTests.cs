using Moment.App.Commands;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

public sealed class TimelineViewModelTests
{
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
    public async Task Todo_edit_uses_the_todo_dialog_and_refreshes_a_conversion_result()
    {
        var todo = TodoRow("项目复盘", new DateOnly(2026, 7, 29));
        var converted = TestData.Row(
            "项目复盘", "2026-07-29T14:30:00+08:00");
        var query = new MutableTimelineQuery(new TimelineSnapshot(
            [todo], [], 0, 0));
        var dialogs = new Dialogs
        {
            AfterTodoEdit = () => query.Snapshot = new TimelineSnapshot(
                [], [converted], 0, 0)
        };
        var vm = Create(query, dialogs: dialogs);
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
        ITodoService? todos = null)
    {
        var reminderDialogs = dialogs ?? new Dialogs();
        var todoDialogs = reminderDialogs as ITodoDialogService ?? new Dialogs();
        return new TimelineViewModel(
            query, new FakeClock("2026-07-29T09:00:00+08:00"),
            service ?? new RecordingReminderService(),
            actions ?? new BlockingActionService(completesImmediately: true),
            todos ?? new RecordingTodoService(),
            reminderDialogs,
            todoDialogs,
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-vm", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));
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
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult(_snapshot);
    }

    private sealed class MutableTimelineQuery(TimelineSnapshot snapshot) : ITimelineQuery
    {
        public TimelineSnapshot Snapshot { get; set; } = snapshot;

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult(Snapshot);
    }

    private sealed class ThrowingTimelineQuery(Exception exception) : ITimelineQuery
    {
        public Task<TimelineSnapshot> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromException<TimelineSnapshot>(exception);
    }

    private sealed class CancelThenReturnQuery(TimelineRow row) : ITimelineQuery
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstCancellationObserved { get; private set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct)
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
        public Action? AfterTodoEdit { get; init; }
        public List<TodoItem> EditedTodos { get; } = [];
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
        public Task EditTodoAsync(TodoItem item, CancellationToken ct)
        {
            EditedTodos.Add(item);
            AfterTodoEdit?.Invoke();
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
