using Hourbit.App.Timeline;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Timeline;

public sealed class EditReminderViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-edit", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    [Fact]
    public void Modified_title_due_kind_importance_and_weekly_recurrence_build_exact_draft()
    {
        var vm = Create();
        vm.Title = "项目复盘";
        vm.DateText = "2026-08-03";
        vm.TimeText = "14:45";
        vm.SelectedKind = ReminderKind.Plan;
        vm.SelectedImportance = ReminderImportance.Important;
        vm.SelectedRecurrence = EditRecurrenceMode.Weekly;
        vm.Weekdays.Single(day => day.Day == DayOfWeek.Monday).IsSelected = true;
        vm.Weekdays.Single(day => day.Day == DayOfWeek.Wednesday).IsSelected = true;

        var valid = vm.TryBuildDraft(out var draft);

        Assert.True(valid);
        Assert.NotNull(draft);
        Assert.Equal("项目复盘", draft.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T14:45:00+08:00"), draft.DueAt);
        Assert.Equal(ReminderKind.Plan, draft.Kind);
        Assert.Equal(ReminderImportance.Important, draft.Importance);
        Assert.Equal(RecurrenceKind.Weekly, draft.Recurrence?.Kind);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday],
            draft.Recurrence?.DaysOfWeek.OrderBy(day => day));
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void Blank_title_is_rejected()
    {
        var vm = Create();
        vm.Title = "  ";

        var valid = vm.TryBuildDraft(out var draft);

        Assert.False(valid);
        Assert.Null(draft);
        Assert.Equal("请输入提醒内容。", vm.ErrorMessage);
    }

    [Theory]
    [InlineData("2026-02-30", "09:00")]
    [InlineData("2026-08-03", "24:10")]
    public void Invalid_date_or_time_is_rejected(string date, string time)
    {
        var vm = Create();
        vm.DateText = date;
        vm.TimeText = time;

        var valid = vm.TryBuildDraft(out var draft);

        Assert.False(valid);
        Assert.Null(draft);
        Assert.Equal("请输入有效的日期和时间。", vm.ErrorMessage);
    }

    [Fact]
    public void Weekly_recurrence_requires_at_least_one_valid_day()
    {
        var vm = Create();
        vm.SelectedRecurrence = EditRecurrenceMode.Weekly;

        var valid = vm.TryBuildDraft(out var draft);

        Assert.False(valid);
        Assert.Null(draft);
        Assert.Equal("请至少选择一个星期几。", vm.ErrorMessage);
    }

    [Fact]
    public async Task Saving_a_timed_edit_uses_the_existing_reminder_service_and_scope()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var refreshed = 0;
        var vm = CreatePersisted(
            reminders,
            todos,
            SeriesScope.ThisAndFuture,
            afterSaved: _ =>
            {
                refreshed++;
                return Task.CompletedTask;
            });
        vm.Title = "项目复盘";
        vm.TimeText = "14:45";
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.SaveAsync();

        var edit = Assert.Single(reminders.Edits);
        Assert.Equal(vm.OccurrenceId, edit.OccurrenceId);
        Assert.Equal("项目复盘", edit.Draft.Title);
        Assert.Equal("14:45", edit.Draft.DueAt.ToString("HH:mm"));
        Assert.Equal(SeriesScope.ThisAndFuture, edit.Scope);
        Assert.Empty(todos.ReminderConversions);
        Assert.Equal(1, refreshed);
        Assert.Equal(1, closes);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Copy_create_success_with_failed_refresh_retries_only_refresh()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = new EditReminderViewModel(
            new ReminderDraft(
                "按时吃饭",
                DateTimeOffset.Parse("2026-08-12T15:43:00+08:00"),
                ReminderKind.Plan,
                ReminderImportance.Normal,
                RecurrenceRule.Daily(new TimeOnly(15, 43))),
            Zone,
            reminders,
            todos,
            _ => ++refreshAttempts == 1
                ? Task.FromException(new InvalidOperationException("刷新失败"))
                : Task.CompletedTask);

        await vm.SaveAsync();
        await vm.SaveAsync();

        var created = Assert.Single(reminders.Created);
        Assert.Equal("按时吃饭", created.Title);
        Assert.Equal("15:43", created.DueAt.ToString("HH:mm"));
        Assert.Equal(2, refreshAttempts);
        Assert.False(vm.IsRefreshOnly);
    }

    [Fact]
    public void Copy_factory_uses_local_today_and_rounds_system_time_up_to_next_minute()
    {
        var source = new TimelineItemViewModel(
            TestData.Row(
                "按时吃饭", "2026-08-09T17:00:00+08:00",
                recurrenceText: "每天"),
            DateTimeOffset.Parse("2026-08-12T15:42:13+08:00"));

        var vm = EditReminderViewModel.CreateCopy(
            source,
            Zone,
            new FakeClock("2026-08-12T15:42:13+08:00"),
            new RecordingReminderService(),
            new RecordingTodoService());

        Assert.Equal("按时吃饭", vm.Title);
        Assert.Equal("2026-08-12", vm.DateText);
        Assert.Equal("15:43", vm.TimeText);
        Assert.Equal(EditRecurrenceMode.Daily, vm.SelectedRecurrence);
        Assert.Equal("新建提醒副本", vm.EditorTitle);
    }

    [Fact]
    public async Task Copy_reminder_without_time_creates_a_new_todo_without_converting_source()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var vm = EditReminderViewModel.CreateCopy(
            new TimelineItemViewModel(
                TestData.Row("买菜", "2026-08-12T17:00:00+08:00"),
                DateTimeOffset.Parse("2026-08-12T15:42:13+08:00")),
            Zone,
            new FakeClock("2026-08-12T15:42:13+08:00"),
            reminders,
            todos);
        vm.TimeText = "";

        await vm.SaveAsync();

        var created = Assert.Single(todos.Created);
        Assert.Equal("买菜", created.Title);
        Assert.Empty(todos.ReminderConversions);
        Assert.Empty(reminders.Created);
    }

    [Theory]
    [InlineData("2026-08-03", "2026-08-03")]
    [InlineData("", null)]
    public async Task Removing_time_converts_to_a_dated_or_undated_todo(
        string dateText,
        string? expectedDate)
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var vm = CreatePersisted(reminders, todos);
        vm.Title = "项目复盘";
        vm.DateText = dateText;
        vm.TimeText = "";
        vm.SelectedImportance = ReminderImportance.Important;

        await vm.SaveAsync();

        var conversion = Assert.Single(todos.ReminderConversions);
        Assert.Equal(vm.OccurrenceId, conversion.OccurrenceId);
        Assert.Equal("项目复盘", conversion.Draft.Title);
        Assert.Equal(
            expectedDate is null ? null : DateOnly.Parse(expectedDate),
            conversion.Draft.DueDate);
        Assert.Equal(ReminderImportance.Important, conversion.Draft.Importance);
        Assert.Equal(SeriesScope.OccurrenceOnly, conversion.Scope);
        Assert.Empty(reminders.Edits);
    }

    [Fact]
    public async Task Recurring_reminder_conversion_requires_an_explicit_scope_choice()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var scopeRequests = 0;
        var vm = CreatePersisted(
            reminders,
            todos,
            recurring: true,
            selectConversionScope: _ =>
            {
                scopeRequests++;
                return Task.FromResult<SeriesScope?>(null);
            });
        vm.TimeText = "";

        await vm.SaveAsync();

        Assert.Equal(1, scopeRequests);
        Assert.Empty(todos.ReminderConversions);
        Assert.Equal("请选择重复提醒的转换范围。", vm.ErrorMessage);
    }

    [Fact]
    public async Task Conversion_failure_keeps_editor_open_and_source_unchanged()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService
        {
            ConversionFailure = new InvalidOperationException("转换失败，原提醒未更改。")
        };
        var vm = CreatePersisted(reminders, todos);
        vm.TimeText = "";
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.SaveAsync();

        Assert.Equal(0, closes);
        Assert.Empty(todos.ReminderConversions);
        Assert.Equal("转换失败，原提醒未更改。", vm.ErrorMessage);
        Assert.Empty(reminders.Edits);
    }

    [Fact]
    public async Task Changed_fields_after_saved_edit_with_failed_refresh_are_persisted_again()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = CreatePersisted(
            reminders,
            todos,
            afterSaved: _ =>
            {
                if (++refreshAttempts == 1)
                    throw new InvalidOperationException("时间轴刷新失败");
                return Task.CompletedTask;
            });

        await vm.SaveAsync();
        vm.Title = "变更后的会议";
        await vm.SaveAsync();

        Assert.Collection(
            reminders.Edits,
            edit => Assert.Equal("会议", edit.Draft.Title),
            edit => Assert.Equal("变更后的会议", edit.Draft.Title));
        Assert.Equal(2, refreshAttempts);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Identical_weekly_edit_with_failed_refresh_does_not_edit_twice()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = CreatePersisted(
            reminders,
            todos,
            recurrenceText: "每周 周一、周三",
            afterSaved: _ =>
            {
                if (++refreshAttempts == 1)
                    throw new InvalidOperationException("时间轴刷新失败");
                return Task.CompletedTask;
            });

        await vm.SaveAsync();
        await vm.SaveAsync();

        Assert.Single(reminders.Edits);
        Assert.Equal(2, refreshAttempts);
    }

    [Fact]
    public async Task Converted_reminder_with_failed_refresh_allows_only_refresh_retry()
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var refreshAttempts = 0;
        var vm = CreatePersisted(
            reminders,
            todos,
            afterSaved: _ =>
            {
                if (++refreshAttempts == 1)
                    throw new InvalidOperationException("时间轴刷新失败");
                return Task.CompletedTask;
            });
        vm.TimeText = "";
        var originalTitle = vm.Title;

        await vm.SaveAsync();

        Assert.False(todos.ReminderSourceExists);
        Assert.True(vm.IsRefreshOnly);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanCancel);
        Assert.Equal("重试刷新", vm.PrimaryActionText);
        Assert.Contains("已转换为待办", vm.ErrorMessage);

        vm.Title = "不得写入已删除源";
        vm.TimeText = "15:00";
        await vm.SaveAsync();

        Assert.Equal(originalTitle, vm.Title);
        Assert.Equal("", vm.TimeText);
        Assert.Single(todos.ReminderConversions);
        Assert.Empty(reminders.Edits);
        Assert.Equal(2, refreshAttempts);
    }

    [Fact]
    public async Task Direct_save_and_save_command_share_one_atomic_busy_gate()
    {
        var reminders = new BlockingReminderService();
        var todos = new RecordingTodoService();
        var vm = CreatePersisted(reminders, todos);

        var first = vm.SaveAsync();
        await reminders.EditEntered.Task;
        var second = vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanCancel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(1, reminders.EditCalls);

        reminders.ReleaseEdit.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, reminders.EditCalls);
    }

    private static EditReminderViewModel Create()
    {
        var item = new TimelineItemViewModel(
            TestData.Row("会议", "2026-07-29T10:30:00+08:00"),
            DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"));
        return new EditReminderViewModel(item, Zone);
    }

    private static EditReminderViewModel CreatePersisted(
        IReminderService reminders,
        RecordingTodoService todos,
        SeriesScope editScope = SeriesScope.OccurrenceOnly,
        bool recurring = false,
        string? recurrenceText = null,
        Func<CancellationToken, Task<SeriesScope?>>? selectConversionScope = null,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        var row = TestData.Row("会议", "2026-08-03T10:30:00+08:00") with
        {
            RecurrenceText = recurrenceText ?? (recurring ? "每天" : null)
        };
        var item = new TimelineItemViewModel(
            row,
            DateTimeOffset.Parse("2026-08-03T09:00:00+08:00"));
        return new EditReminderViewModel(
            item,
            Zone,
            reminders,
            todos,
            editScope,
            selectConversionScope,
            afterSaved);
    }

    private sealed class RecordingReminderService : IReminderService
    {
        public List<(Guid OccurrenceId, ReminderDraft Draft, SeriesScope Scope)> Edits { get; } = [];
        public List<ReminderDraft> Created { get; } = [];

        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            return Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        }

        public Task EditAsync(
            Guid occurrenceId,
            ReminderDraft draft,
            SeriesScope scope,
            CancellationToken ct)
        {
            Edits.Add((occurrenceId, draft, scope));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingTodoService : ITodoService
    {
        public List<(Guid OccurrenceId, TodoDraft Draft, SeriesScope Scope)> ReminderConversions { get; } = [];
        public List<TodoDraft> Created { get; } = [];
        public Exception? ConversionFailure { get; init; }
        public bool ReminderSourceExists { get; private set; } = true;

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            return Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title, DateTimeOffset.UtcNow,
                draft.DueDate, draft.Importance, false, null));
        }
        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task CompleteAsync(Guid todoId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid todoId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ConvertToReminderAsync(Guid todoId, ReminderDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ConvertToTodoAsync(Guid occurrenceId, TodoDraft draft, CancellationToken ct) =>
            ConvertToTodoAsync(occurrenceId, draft, SeriesScope.OccurrenceOnly, ct);
        public Task ConvertToTodoAsync(
            Guid occurrenceId,
            TodoDraft draft,
            SeriesScope scope,
            CancellationToken ct)
        {
            if (ConversionFailure is not null)
                throw ConversionFailure;
            if (!ReminderSourceExists)
                throw new InvalidOperationException("提醒源已删除。");
            ReminderConversions.Add((occurrenceId, draft, scope));
            ReminderSourceExists = false;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingReminderService : IReminderService
    {
        public TaskCompletionSource EditEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEdit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EditCalls { get; private set; }

        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public async Task EditAsync(
            Guid occurrenceId,
            ReminderDraft draft,
            SeriesScope scope,
            CancellationToken ct)
        {
            EditCalls++;
            EditEntered.TrySetResult();
            await ReleaseEdit.Task.WaitAsync(ct);
        }
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
