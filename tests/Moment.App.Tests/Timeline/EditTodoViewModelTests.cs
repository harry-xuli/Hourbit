using Moment.App.Timeline;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;

namespace Moment.App.Tests.Timeline;

public sealed class EditTodoViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-todo-edit", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    [Fact]
    public async Task Saving_without_a_time_edits_optional_date_and_importance()
    {
        var service = new RecordingTodoService();
        var refreshed = 0;
        var vm = Create(service, _ =>
        {
            refreshed++;
            return Task.CompletedTask;
        });
        vm.Title = "整理发布清单";
        vm.DateText = "";
        vm.SelectedImportance = ReminderImportance.Important;
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.SaveAsync();

        var edit = Assert.Single(service.Edits);
        Assert.Equal(vm.TodoId, edit.TodoId);
        Assert.Equal("整理发布清单", edit.Draft.Title);
        Assert.Null(edit.Draft.DueDate);
        Assert.Equal(ReminderImportance.Important, edit.Draft.Importance);
        Assert.Empty(service.ReminderConversions);
        Assert.Equal(1, refreshed);
        Assert.Equal(1, closes);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Copy_create_success_with_failed_refresh_retries_only_refresh()
    {
        var service = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = new EditTodoViewModel(
            new TodoDraft("天气不错", null, ReminderImportance.Important),
            Zone,
            service,
            _ => ++refreshAttempts == 1
                ? Task.FromException(new InvalidOperationException("刷新失败"))
                : Task.CompletedTask);

        await vm.SaveAsync();
        await vm.SaveAsync();

        var created = Assert.Single(service.Created);
        Assert.Equal("天气不错", created.Title);
        Assert.Null(created.DueDate);
        Assert.Equal(2, refreshAttempts);
        Assert.False(vm.IsRefreshOnly);
    }

    [Fact]
    public void Copy_factory_preserves_todo_fields_and_keeps_time_empty()
    {
        var source = new TodoItem(
            Guid.NewGuid(), "天气不错",
            DateTimeOffset.Parse("2026-08-10T09:00:00+08:00"),
            new DateOnly(2026, 8, 15),
            ReminderImportance.Important, false, null);

        var vm = EditTodoViewModel.CreateCopy(
            source, Zone, new RecordingTodoService(),
            new RecordingReminderService());

        Assert.Equal("天气不错", vm.Title);
        Assert.Equal("2026-08-15", vm.DateText);
        Assert.Equal("", vm.TimeText);
        Assert.Equal(ReminderImportance.Important, vm.SelectedImportance);
        Assert.Equal("新建待办副本", vm.EditorTitle);
    }

    [Fact]
    public async Task Copy_todo_with_time_creates_a_new_reminder_without_converting_source()
    {
        var todos = new RecordingTodoService();
        var reminders = new RecordingReminderService();
        var vm = EditTodoViewModel.CreateCopy(
            new TodoItem(
                Guid.NewGuid(), "开会", DateTimeOffset.UtcNow,
                new DateOnly(2026, 8, 12), ReminderImportance.Normal,
                false, null),
            Zone,
            todos,
            reminders);
        vm.TimeText = "16:00";

        await vm.SaveAsync();

        var created = Assert.Single(reminders.Created);
        Assert.Equal("开会", created.Title);
        Assert.Empty(todos.ReminderConversions);
        Assert.True(todos.SourceExists);
    }

    [Fact]
    public async Task Adding_a_time_converts_the_todo_to_a_reminder_atomically()
    {
        var service = new RecordingTodoService();
        var vm = Create(service);
        vm.Title = "项目复盘";
        vm.DateText = "2026-08-05";
        vm.TimeText = "14:30";
        vm.SelectedImportance = ReminderImportance.Important;

        await vm.SaveAsync();

        var conversion = Assert.Single(service.ReminderConversions);
        Assert.Equal(vm.TodoId, conversion.TodoId);
        Assert.Equal("项目复盘", conversion.Draft.Title);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-05T14:30:00+08:00"),
            conversion.Draft.DueAt);
        Assert.Equal(ReminderKind.Plan, conversion.Draft.Kind);
        Assert.Equal(ReminderImportance.Important, conversion.Draft.Importance);
        Assert.Null(conversion.Draft.Recurrence);
        Assert.Empty(service.Edits);
    }

    [Fact]
    public async Task Time_without_a_date_is_rejected_without_persistence()
    {
        var service = new RecordingTodoService();
        var vm = Create(service);
        vm.DateText = "";
        vm.TimeText = "14:30";

        await vm.SaveAsync();

        Assert.Equal("添加时间时请同时选择日期。", vm.ErrorMessage);
        Assert.Empty(service.Edits);
        Assert.Empty(service.ReminderConversions);
    }

    [Fact]
    public async Task Conversion_failure_keeps_editor_open_and_source_unchanged()
    {
        var service = new RecordingTodoService
        {
            ConversionFailure = new InvalidOperationException("转换失败，原待办未更改。")
        };
        var vm = Create(service);
        vm.TimeText = "14:30";
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.SaveAsync();

        Assert.Equal(0, closes);
        Assert.Equal("转换失败，原待办未更改。", vm.ErrorMessage);
        Assert.Empty(service.ReminderConversions);
        Assert.Empty(service.Edits);
    }

    [Fact]
    public async Task Complete_and_delete_actions_use_the_todo_service()
    {
        var completeService = new RecordingTodoService();
        var completeVm = Create(completeService);
        await completeVm.CompleteAsync();

        var deleteService = new RecordingTodoService();
        var deleteVm = Create(deleteService);
        await deleteVm.DeleteAsync();

        Assert.Equal(completeVm.TodoId, Assert.Single(completeService.Completed));
        Assert.Equal(deleteVm.TodoId, Assert.Single(deleteService.Deleted));
    }

    [Fact]
    public async Task Delete_after_saved_edit_with_failed_refresh_is_not_swallowed()
    {
        var service = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = Create(service, _ =>
        {
            if (++refreshAttempts == 1)
                throw new InvalidOperationException("时间轴刷新失败");
            return Task.CompletedTask;
        });

        await vm.SaveAsync();
        await vm.DeleteAsync();

        Assert.Single(service.Edits);
        Assert.Equal(vm.TodoId, Assert.Single(service.Deleted));
        Assert.Equal(2, refreshAttempts);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Converted_todo_with_failed_refresh_allows_only_refresh_retry()
    {
        var service = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = Create(service, _ =>
        {
            if (++refreshAttempts == 1)
                throw new InvalidOperationException("时间轴刷新失败");
            return Task.CompletedTask;
        });
        vm.TimeText = "14:30";
        var originalTitle = vm.Title;
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.SaveAsync();

        Assert.False(service.SourceExists);
        Assert.True(vm.IsRefreshOnly);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanCancel);
        Assert.Equal("重试刷新", vm.PrimaryActionText);
        Assert.Contains("已转换为提醒", vm.ErrorMessage);

        vm.Title = "不得写入已删除源";
        await vm.DeleteAsync();
        await vm.CompleteAsync();
        await vm.SaveAsync();

        Assert.Equal(originalTitle, vm.Title);
        Assert.Single(service.ReminderConversions);
        Assert.Empty(service.Deleted);
        Assert.Empty(service.Completed);
        Assert.Equal(2, refreshAttempts);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task Deleted_todo_with_failed_refresh_never_reuses_deleted_source_id()
    {
        var service = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = Create(service, _ =>
        {
            if (++refreshAttempts == 1)
                throw new InvalidOperationException("时间轴刷新失败");
            return Task.CompletedTask;
        });

        await vm.DeleteAsync();

        Assert.False(vm.CanCancel);

        await vm.CompleteAsync();
        await vm.SaveAsync();

        Assert.False(service.SourceExists);
        Assert.Single(service.Deleted);
        Assert.Empty(service.Completed);
        Assert.Empty(service.Edits);
        Assert.Equal(2, refreshAttempts);
    }

    [Fact]
    public async Task Save_and_delete_share_one_atomic_busy_gate()
    {
        var service = new BlockingTodoService();
        var vm = Create(service);

        var save = vm.SaveAsync();
        await service.EditEntered.Task;
        var delete = vm.DeleteAsync();

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanCancel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.CompleteCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal(0, service.DeleteCalls);

        service.ReleaseEdit.SetResult();
        await Task.WhenAll(save, delete);

        Assert.Equal(1, service.EditCalls);
        Assert.Equal(0, service.DeleteCalls);
    }

    [Fact]
    public async Task Complete_and_delete_commands_share_one_atomic_busy_gate()
    {
        var service = new BlockingTodoService();
        var vm = Create(service);

        var complete = vm.CompleteCommand.ExecuteAsync(null);
        await service.CompleteEntered.Task;
        var delete = vm.DeleteCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.CompleteCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal(1, service.CompleteCalls);
        Assert.Equal(0, service.DeleteCalls);

        service.ReleaseComplete.SetResult();
        await Task.WhenAll(complete, delete);

        Assert.Equal(1, service.CompleteCalls);
        Assert.Equal(0, service.DeleteCalls);
    }

    private static EditTodoViewModel Create(
        ITodoService service,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        var todo = new TodoItem(
            Guid.Parse("10000000-0000-0000-0000-000000000005"),
            "项目复盘",
            DateTimeOffset.Parse("2026-08-01T09:00:00+08:00"),
            new DateOnly(2026, 8, 5),
            ReminderImportance.Normal,
            false,
            null);
        return new EditTodoViewModel(todo, Zone, service, afterSaved);
    }

    private sealed class RecordingTodoService : ITodoService
    {
        public List<TodoDraft> Created { get; } = [];
        public List<(Guid TodoId, TodoDraft Draft)> Edits { get; } = [];
        public List<(Guid TodoId, ReminderDraft Draft)> ReminderConversions { get; } = [];
        public List<Guid> Completed { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Exception? ConversionFailure { get; init; }
        public bool SourceExists { get; private set; } = true;

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            return Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title,
                DateTimeOffset.Parse("2026-08-12T15:42:13+08:00"),
                draft.DueDate, draft.Importance, false, null));
        }

        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct)
        {
            EnsureSourceExists();
            Edits.Add((todoId, draft));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid todoId, CancellationToken ct)
        {
            EnsureSourceExists();
            Completed.Add(todoId);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid todoId, CancellationToken ct)
        {
            EnsureSourceExists();
            Deleted.Add(todoId);
            SourceExists = false;
            return Task.CompletedTask;
        }

        public Task ConvertToReminderAsync(
            Guid todoId,
            ReminderDraft draft,
            CancellationToken ct)
        {
            if (ConversionFailure is not null)
                throw ConversionFailure;
            EnsureSourceExists();
            ReminderConversions.Add((todoId, draft));
            SourceExists = false;
            return Task.CompletedTask;
        }

        public Task ConvertToTodoAsync(Guid occurrenceId, TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ConvertToTodoAsync(
            Guid occurrenceId,
            TodoDraft draft,
            SeriesScope scope,
            CancellationToken ct) => throw new NotSupportedException();

        private void EnsureSourceExists()
        {
            if (!SourceExists)
                throw new InvalidOperationException("待办源已删除。");
        }
    }

    private sealed class RecordingReminderService : IReminderService
    {
        public List<ReminderDraft> Created { get; } = [];
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            return Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        }
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingTodoService : ITodoService
    {
        public TaskCompletionSource EditEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEdit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseComplete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EditCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public async Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct)
        {
            EditCalls++;
            EditEntered.TrySetResult();
            await ReleaseEdit.Task.WaitAsync(ct);
        }
        public async Task CompleteAsync(Guid todoId, CancellationToken ct)
        {
            CompleteCalls++;
            CompleteEntered.TrySetResult();
            await ReleaseComplete.Task.WaitAsync(ct);
        }
        public Task DeleteAsync(Guid todoId, CancellationToken ct)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }
        public Task ConvertToReminderAsync(
            Guid todoId, ReminderDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ConvertToTodoAsync(
            Guid occurrenceId,
            TodoDraft draft,
            SeriesScope scope,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
