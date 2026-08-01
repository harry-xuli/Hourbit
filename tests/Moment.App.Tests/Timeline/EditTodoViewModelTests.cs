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

    private static EditTodoViewModel Create(
        RecordingTodoService service,
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
        public List<(Guid TodoId, TodoDraft Draft)> Edits { get; } = [];
        public List<(Guid TodoId, ReminderDraft Draft)> ReminderConversions { get; } = [];
        public List<Guid> Completed { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Exception? ConversionFailure { get; init; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct)
        {
            Edits.Add((todoId, draft));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid todoId, CancellationToken ct)
        {
            Completed.Add(todoId);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid todoId, CancellationToken ct)
        {
            Deleted.Add(todoId);
            return Task.CompletedTask;
        }

        public Task ConvertToReminderAsync(
            Guid todoId,
            ReminderDraft draft,
            CancellationToken ct)
        {
            if (ConversionFailure is not null)
                throw ConversionFailure;
            ReminderConversions.Add((todoId, draft));
            return Task.CompletedTask;
        }

        public Task ConvertToTodoAsync(Guid occurrenceId, TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ConvertToTodoAsync(
            Guid occurrenceId,
            TodoDraft draft,
            SeriesScope scope,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
