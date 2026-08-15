using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Recurrence;
using Hourbit.Core.Services;
using Hourbit.TestSupport;

namespace Hourbit.Core.Tests.Services;

public sealed class TodoServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 2, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo ChinaZone =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    [Fact]
    public async Task Create_uses_the_clock_and_persists_a_pending_todo_without_signaling()
    {
        var todos = new FakeTodoRepository();
        var signal = new RecordingSignal();
        var service = CreateService(todos: todos, signal: signal);

        var created = await service.CreateAsync(
            new TodoDraft("  提交报告  ", new DateOnly(2026, 8, 5),
                ReminderImportance.Important), default);

        Assert.Equal("提交报告", created.Title);
        Assert.Equal(Now, created.CreatedAt);
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);
        Assert.Equal(created, await todos.GetAsync(created.Id, default));
        Assert.Equal(0, signal.RefreshCount);
    }

    [Fact]
    public async Task Edit_preserves_identity_creation_and_completion_fields()
    {
        var todos = new FakeTodoRepository();
        var completedAt = Now.AddMinutes(-5);
        var existing = new TodoItem(
            Guid.NewGuid(), "编辑前", Now.AddHours(-1), null,
            ReminderImportance.Normal, true, completedAt);
        await todos.SaveAsync(existing, default);
        var service = CreateService(todos: todos);

        await service.EditAsync(existing.Id,
            new TodoDraft("编辑后", new DateOnly(2026, 8, 8),
                ReminderImportance.Important), default);

        var edited = await todos.GetAsync(existing.Id, default);
        Assert.Equal(existing.Id, edited!.Id);
        Assert.Equal(existing.CreatedAt, edited.CreatedAt);
        Assert.Equal(completedAt, edited.CompletedAt);
        Assert.True(edited.IsCompleted);
        Assert.Equal("编辑后", edited.Title);
        Assert.Equal(new DateOnly(2026, 8, 8), edited.DueDate);
        Assert.Equal(ReminderImportance.Important, edited.Importance);
    }

    [Fact]
    public async Task Complete_is_idempotent_and_keeps_the_first_completion_timestamp()
    {
        var todos = new FakeTodoRepository();
        var existing = PendingTodo("完成我");
        await todos.SaveAsync(existing, default);
        var service = CreateService(todos: todos);

        await service.CompleteAsync(existing.Id, default);
        var first = await todos.GetAsync(existing.Id, default);
        await service.CompleteAsync(existing.Id, default);

        Assert.True(first!.IsCompleted);
        Assert.Equal(Now, first.CompletedAt);
        Assert.Equal(first, await todos.GetAsync(existing.Id, default));
    }

    [Fact]
    public async Task Completing_a_recurring_todo_generates_the_next_occurrence()
    {
        var todos = new FakeTodoRepository();
        var recurring = new TodoItem(
            Guid.NewGuid(), "每天锻炼", Now.AddHours(-1),
            new DateOnly(2026, 8, 3), ReminderImportance.Normal, false, null,
            RecurrenceRule.Daily(TimeOnly.MinValue));
        await todos.SaveAsync(recurring, default);
        var service = CreateService(todos: todos);

        await service.CompleteAsync(recurring.Id, default);

        var all = await todos.GetAllAsync(default);
        Assert.Equal(2, all.Count);
        var completed = all.Single(item => item.Id == recurring.Id);
        Assert.True(completed.IsCompleted);
        var next = all.Single(item => item.Id != recurring.Id);
        Assert.False(next.IsCompleted);
        Assert.Equal("每天锻炼", next.Title);
        Assert.Equal(new DateOnly(2026, 8, 4), next.DueDate);
        Assert.Equal(RecurrenceKind.Daily, next.Recurrence!.Kind);
    }

    [Fact]
    public async Task Completing_a_weekdays_recurring_todo_skips_the_weekend()
    {
        var todos = new FakeTodoRepository();
        var recurring = new TodoItem(
            Guid.NewGuid(), "工作日复盘", Now.AddHours(-1),
            new DateOnly(2026, 8, 7), // Friday
            ReminderImportance.Normal, false, null,
            RecurrenceRule.Weekdays(TimeOnly.MinValue));
        await todos.SaveAsync(recurring, default);
        var service = CreateService(todos: todos);

        await service.CompleteAsync(recurring.Id, default);

        var next = (await todos.GetAllAsync(default))
            .Single(item => item.Id != recurring.Id);
        Assert.Equal(new DateOnly(2026, 8, 10), next.DueDate); // next Monday
        Assert.Equal(RecurrenceKind.Weekdays, next.Recurrence!.Kind);
    }

    [Fact]
    public async Task Edit_and_complete_interleaving_preserves_both_detail_and_completion_changes()
    {
        var inner = new FakeTodoRepository();
        var existing = PendingTodo("编辑前");
        await inner.SaveAsync(existing, default);
        var interleaving = new PauseAfterSnapshotTodoRepository(inner);
        var editService = CreateService(todos: interleaving);

        var edit = editService.EditAsync(existing.Id,
            new TodoDraft("编辑后", new DateOnly(2026, 8, 9),
                ReminderImportance.Important), default);
        await interleaving.SnapshotRead;
        await CreateService(todos: inner).CompleteAsync(existing.Id, default);
        interleaving.ReleaseEdit();
        await edit;

        var persisted = await inner.GetAsync(existing.Id, default);
        Assert.Equal("编辑后", persisted!.Title);
        Assert.Equal(new DateOnly(2026, 8, 9), persisted.DueDate);
        Assert.Equal(ReminderImportance.Important, persisted.Importance);
        Assert.True(persisted.IsCompleted);
        Assert.Equal(Now, persisted.CompletedAt);
    }

    [Fact]
    public async Task Two_complete_interleaving_keeps_the_first_completion_timestamp()
    {
        var inner = new FakeTodoRepository();
        var existing = PendingTodo("只完成一次");
        await inner.SaveAsync(existing, default);
        var interleaving = new OrderedCompleteTodoRepository(inner);
        var first = CreateService(
            todos: interleaving, clock: new FakeClock(Now));
        var later = Now.AddHours(1);
        var second = CreateService(
            todos: interleaving, clock: new FakeClock(later));

        var firstCompletion = first.CompleteAsync(existing.Id, default);
        await interleaving.FirstSnapshotRead;
        var secondCompletion = second.CompleteAsync(existing.Id, default);
        await Task.WhenAll(firstCompletion, secondCompletion);

        var persisted = await inner.GetAsync(existing.Id, default);
        Assert.True(persisted!.IsCompleted);
        Assert.Equal(Now, persisted.CompletedAt);
    }

    [Fact]
    public async Task Delete_removes_the_todo_without_signaling_the_reminder_scheduler()
    {
        var todos = new FakeTodoRepository();
        var existing = PendingTodo("删除我");
        await todos.SaveAsync(existing, default);
        var signal = new RecordingSignal();
        var service = CreateService(todos: todos, signal: signal);

        await service.DeleteAsync(existing.Id, default);

        Assert.Null(await todos.GetAsync(existing.Id, default));
        Assert.Equal(Now, todos.LastDeletedAt);
        Assert.Equal(0, signal.RefreshCount);
    }

    [Theory]
    [MemberData(nameof(InvalidTodoDrafts))]
    public async Task Todo_operations_reject_invalid_drafts_before_repository_access(
        TodoDraft draft)
    {
        var todos = new RecordingTodoRepository();
        var service = CreateService(todos: todos);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAsync(draft, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.EditAsync(Guid.NewGuid(), draft, default));

        Assert.Equal(0, todos.CallCount);
    }

    [Fact]
    public async Task Convert_pending_todo_builds_a_scheduled_reminder_then_signals_after_commit()
    {
        var events = new List<string>();
        var source = PendingTodo("原待办", ReminderImportance.Normal);
        var todos = new FakeTodoRepository();
        await todos.SaveAsync(source, default);
        var store = new RecordingConversionStore(events,
            new ItemConversionResult(SchedulingChanged: true));
        var service = CreateService(todos, store: store,
            signal: new RecordingSignal(events));
        var draft = new ReminderDraft(
            "已转提醒", Now.AddHours(2), ReminderKind.Alarm,
            ReminderImportance.Important, RecurrenceRule.Daily(new TimeOnly(11, 0)));

        await service.ConvertToReminderAsync(source.Id, draft, default);

        Assert.Equal(["convert-todo", "refresh"], events);
        var request = Assert.IsType<TodoToReminderConversion>(store.LastRequest);
        Assert.Equal(source, request.Source);
        Assert.Equal(source.CreatedAt, request.DestinationItem.CreatedAt);
        Assert.Equal("已转提醒", request.DestinationItem.Title);
        Assert.Equal(ReminderImportance.Important,
            request.DestinationItem.Importance);
        Assert.Equal(RecurrenceKind.Daily,
            request.DestinationItem.Recurrence!.Kind);
        Assert.Equal(request.DestinationItem.Id,
            request.DestinationOccurrence.ItemId);
        Assert.Equal(OccurrenceState.Scheduled,
            request.DestinationOccurrence.State);
        Assert.Null(request.DestinationOccurrence.HandledAt);
    }

    [Fact]
    public async Task Convert_completed_todo_builds_a_completed_reminder_that_never_signals()
    {
        var completedAt = Now.AddMinutes(-10);
        var source = new TodoItem(
            Guid.NewGuid(), "已完成待办", Now.AddHours(-1), null,
            ReminderImportance.Important, true, completedAt);
        var todos = new FakeTodoRepository();
        await todos.SaveAsync(source, default);
        var store = new RecordingConversionStore([], new ItemConversionResult(false));
        var signal = new RecordingSignal();
        var service = CreateService(todos, store: store, signal: signal);

        await service.ConvertToReminderAsync(source.Id,
            new ReminderDraft("已完成待办", Now.AddMinutes(-30), ReminderKind.Plan,
                ReminderImportance.Important, RecurrenceRule.Weekdays(new TimeOnly(10, 0))),
            default);

        var request = Assert.IsType<TodoToReminderConversion>(store.LastRequest);
        Assert.Equal(OccurrenceState.Completed,
            request.DestinationOccurrence.State);
        Assert.Equal(completedAt, request.DestinationOccurrence.HandledAt);
        Assert.Equal(0, signal.RefreshCount);
    }

    [Fact]
    public async Task Convert_pending_todo_rejects_a_past_due_time_without_conversion_or_signal()
    {
        var source = PendingTodo("未完成待办");
        var todos = new FakeTodoRepository();
        await todos.SaveAsync(source, default);
        var store = new RecordingConversionStore([], new ItemConversionResult(true));
        var signal = new RecordingSignal();
        var service = CreateService(todos, store: store, signal: signal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ConvertToReminderAsync(source.Id,
                new ReminderDraft(source.Title, Now.AddMinutes(-1),
                    ReminderKind.Plan, source.Importance, null), default));

        Assert.Null(store.LastRequest);
        Assert.Equal(source, await todos.GetAsync(source.Id, default));
        Assert.Equal(0, signal.RefreshCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Convert_completed_reminder_builds_a_dated_or_undated_completed_todo(
        bool isDated)
    {
        var completedAt = Now.AddMinutes(-15);
        var source = Reminder(
            OccurrenceState.Completed, completedAt,
            RecurrenceRule.Daily(new TimeOnly(9, 30)));
        var reminders = new FakeReminderRepository();
        await reminders.AddAsync(source);
        var store = new RecordingConversionStore([], new ItemConversionResult(false));
        var signal = new RecordingSignal();
        var service = CreateService(reminders: reminders, store: store, signal: signal);
        DateOnly? dueDate =
            isDated ? new DateOnly(2026, 8, 4) : null;

        await service.ConvertToTodoAsync(source.Occurrence.Id,
            new TodoDraft("已转待办", dueDate, ReminderImportance.Important),
            default);

        var request = Assert.IsType<ReminderToTodoConversion>(store.LastRequest);
        Assert.Equal(SeriesScope.OccurrenceOnly, request.Scope);
        Assert.Null(request.ContinuationOccurrence);
        Assert.Equal(source.Item.CreatedAt, request.Destination.CreatedAt);
        Assert.Equal(dueDate, request.Destination.DueDate);
        Assert.True(request.Destination.IsCompleted);
        Assert.Equal(completedAt, request.Destination.CompletedAt);
        Assert.Equal("已转待办", request.Destination.Title);
        Assert.Equal(ReminderImportance.Important,
            request.Destination.Importance);
        Assert.Equal(0, signal.RefreshCount);
    }

    [Fact]
    public async Task Convert_recurring_occurrence_only_requests_a_unique_series_continuation()
    {
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        var reminders = new FakeReminderRepository();
        await reminders.AddAsync(source);
        var store = new RecordingConversionStore([], new ItemConversionResult(true));
        var service = CreateService(reminders: reminders, store: store);

        await service.ConvertToTodoAsync(source.Occurrence.Id,
            new TodoDraft(source.Item.Title, null, source.Item.Importance),
            SeriesScope.OccurrenceOnly, default);

        var request = Assert.IsType<ReminderToTodoConversion>(store.LastRequest);
        Assert.Equal(SeriesScope.OccurrenceOnly, request.Scope);
        Assert.Equal(source.Item.Id, request.ContinuationOccurrence!.ItemId);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(8)),
            request.ContinuationOccurrence.DueAt);
        Assert.Equal(OccurrenceState.Scheduled,
            request.ContinuationOccurrence.State);
    }

    [Fact]
    public async Task Convert_this_and_future_requests_tail_removal_without_a_continuation()
    {
        var source = Reminder(
            OccurrenceState.Scheduled, null,
            RecurrenceRule.Daily(new TimeOnly(10, 0)));
        var reminders = new FakeReminderRepository();
        await reminders.AddAsync(source);
        var store = new RecordingConversionStore([], new ItemConversionResult(true));
        var service = CreateService(reminders: reminders, store: store);

        await service.ConvertToTodoAsync(source.Occurrence.Id,
            new TodoDraft(source.Item.Title, new DateOnly(2026, 8, 2),
                source.Item.Importance),
            SeriesScope.ThisAndFuture, default);

        var request = Assert.IsType<ReminderToTodoConversion>(store.LastRequest);
        Assert.Equal(SeriesScope.ThisAndFuture, request.Scope);
        Assert.Null(request.ContinuationOccurrence);
    }

    [Fact]
    public async Task Failed_conversion_never_signals_and_fake_transaction_keeps_the_source_only()
    {
        var source = PendingTodo("保持原样");
        var todos = new FakeTodoRepository();
        await todos.SaveAsync(source, default);
        var store = new RollbackFakeConversionStore(source);
        var signal = new RecordingSignal();
        var service = CreateService(todos, store: store, signal: signal);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertToReminderAsync(source.Id,
                new ReminderDraft(source.Title, Now.AddHours(1),
                    ReminderKind.Alarm, source.Importance, null), default));

        Assert.Equal([source.Id], store.TodoIds);
        Assert.Empty(store.ReminderOccurrenceIds);
        Assert.Equal(0, signal.RefreshCount);
    }

    public static TheoryData<TodoDraft> InvalidTodoDrafts =>
    [
        new TodoDraft("", null, ReminderImportance.Normal),
        new TodoDraft(new string('a', 201), null, ReminderImportance.Normal),
        new TodoDraft("错误重要性", null, (ReminderImportance)99)
    ];

    private static TodoService CreateService(
        ITodoRepository? todos = null,
        IReminderRepository? reminders = null,
        IItemConversionStore? store = null,
        ISchedulerSignal? signal = null,
        IClock? clock = null) =>
        new(
            todos ?? new FakeTodoRepository(),
            reminders ?? new FakeReminderRepository(),
            store ?? new RecordingConversionStore([], new ItemConversionResult(false)),
            new RecurrenceCalculator(),
            signal ?? new RecordingSignal(),
            clock ?? new FakeClock(Now),
            ChinaZone);

    private static TodoItem PendingTodo(
        string title,
        ReminderImportance importance = ReminderImportance.Normal) =>
        new(Guid.NewGuid(), title, Now.AddHours(-1), null,
            importance, false, null);

    private static ScheduledReminder Reminder(
        OccurrenceState state,
        DateTimeOffset? handledAt,
        RecurrenceRule? recurrence)
    {
        var item = new ReminderItem(
            Guid.NewGuid(), "原提醒", ReminderKind.Plan,
            ReminderImportance.Normal, Now.AddHours(-1), recurrence);
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), item.Id, Now.AddHours(1), state, handledAt, null);
        return new ScheduledReminder(item, occurrence);
    }

    private sealed class RecordingSignal : ISchedulerSignal
    {
        private readonly List<string>? _events;

        public RecordingSignal(List<string>? events = null) => _events = events;

        public int RefreshCount { get; private set; }

        public void Refresh()
        {
            RefreshCount++;
            _events?.Add("refresh");
        }
    }

    private sealed class RecordingConversionStore(
        List<string> events,
        ItemConversionResult result) : IItemConversionStore
    {
        public object? LastRequest { get; private set; }

        public Task<ItemConversionResult> ConvertTodoToReminderAsync(
            TodoToReminderConversion request,
            CancellationToken ct)
        {
            events.Add("convert-todo");
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<ItemConversionResult> ConvertReminderToTodoAsync(
            ReminderToTodoConversion request,
            CancellationToken ct)
        {
            events.Add("convert-reminder");
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class RollbackFakeConversionStore(TodoItem source)
        : IItemConversionStore
    {
        private Dictionary<Guid, TodoItem> _todos = new() { [source.Id] = source };
        private Dictionary<Guid, ReminderOccurrence> _reminders = [];

        public IReadOnlyCollection<Guid> TodoIds => _todos.Keys;
        public IReadOnlyCollection<Guid> ReminderOccurrenceIds => _reminders.Keys;

        public Task<ItemConversionResult> ConvertTodoToReminderAsync(
            TodoToReminderConversion request,
            CancellationToken ct)
        {
            var todos = new Dictionary<Guid, TodoItem>(_todos);
            var reminders = new Dictionary<Guid, ReminderOccurrence>(_reminders)
            {
                [request.DestinationOccurrence.Id] = request.DestinationOccurrence
            };
            _ = reminders;
            _ = todos.Remove(request.Source.Id);
            throw new InvalidOperationException("injected after destination insert");
        }

        public Task<ItemConversionResult> ConvertReminderToTodoAsync(
            ReminderToTodoConversion request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTodoRepository : ITodoRepository
    {
        public int CallCount { get; private set; }

        public Task SaveAsync(TodoItem item, CancellationToken ct) => Called();
        public Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<TodoItem?>(null);
        }
        public Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<TodoItem>>([]);
        }
        public Task UpdateAsync(TodoItem item, CancellationToken ct) => Called();
        public Task SetCompletedAsync(Guid id, bool isCompleted,
            DateTimeOffset? completedAt, CancellationToken ct) => Called();
        public Task DeleteAsync(Guid id, DateTimeOffset deletedAt,
            CancellationToken ct) => Called();

        private Task Called()
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PauseAfterSnapshotTodoRepository(
        ITodoRepository inner) : ITodoRepository
    {
        private readonly TaskCompletionSource _snapshotRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SnapshotRead => _snapshotRead.Task;

        public void ReleaseEdit() => _release.TrySetResult();

        public Task SaveAsync(TodoItem item, CancellationToken ct) =>
            inner.SaveAsync(item, ct);

        public async Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
        {
            var snapshot = await inner.GetAsync(id, ct);
            _snapshotRead.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return snapshot;
        }

        public Task<IReadOnlyList<TodoItem>> GetAllAsync(
            CancellationToken ct) => inner.GetAllAsync(ct);
        public Task UpdateAsync(TodoItem item, CancellationToken ct) =>
            inner.UpdateAsync(item, ct);
        public Task SetCompletedAsync(Guid id, bool isCompleted,
            DateTimeOffset? completedAt, CancellationToken ct) =>
            inner.SetCompletedAsync(id, isCompleted, completedAt, ct);
        public Task DeleteAsync(Guid id, DateTimeOffset deletedAt,
            CancellationToken ct) =>
            inner.DeleteAsync(id, deletedAt, ct);
    }

    private sealed class OrderedCompleteTodoRepository(
        ITodoRepository inner) : ITodoRepository
    {
        private readonly TaskCompletionSource _firstSnapshotRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondSnapshotRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCompletionWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _writeCount;

        public Task FirstSnapshotRead => _firstSnapshotRead.Task;

        public Task SaveAsync(TodoItem item, CancellationToken ct) =>
            inner.SaveAsync(item, ct);

        public async Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
        {
            var snapshot = await inner.GetAsync(id, ct);
            var read = Interlocked.Increment(ref _readCount);
            if (read == 1)
            {
                _firstSnapshotRead.TrySetResult();
                await _secondSnapshotRead.Task.WaitAsync(ct);
            }
            else if (read == 2)
            {
                _secondSnapshotRead.TrySetResult();
                await _firstCompletionWritten.Task.WaitAsync(ct);
            }
            return snapshot;
        }

        public Task<IReadOnlyList<TodoItem>> GetAllAsync(
            CancellationToken ct) => inner.GetAllAsync(ct);
        public Task UpdateAsync(TodoItem item, CancellationToken ct) =>
            inner.UpdateAsync(item, ct);

        public async Task SetCompletedAsync(Guid id, bool isCompleted,
            DateTimeOffset? completedAt, CancellationToken ct)
        {
            await inner.SetCompletedAsync(id, isCompleted, completedAt, ct);
            if (Interlocked.Increment(ref _writeCount) == 1)
                _firstCompletionWritten.TrySetResult();
        }

        public Task DeleteAsync(Guid id, DateTimeOffset deletedAt,
            CancellationToken ct) =>
            inner.DeleteAsync(id, deletedAt, ct);
    }
}
