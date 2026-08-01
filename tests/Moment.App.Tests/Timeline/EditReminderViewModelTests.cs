using Moment.App.Timeline;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

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
        vm.WeeklyDaysText = "周一、周三";

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
        vm.WeeklyDaysText = "每个月";

        var valid = vm.TryBuildDraft(out var draft);

        Assert.False(valid);
        Assert.Null(draft);
        Assert.Equal("每周重复请至少选择一天。", vm.ErrorMessage);
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

    private static EditReminderViewModel Create()
    {
        var item = new TimelineItemViewModel(
            TestData.Row("会议", "2026-07-29T10:30:00+08:00"),
            DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"));
        return new EditReminderViewModel(item, Zone);
    }

    private static EditReminderViewModel CreatePersisted(
        RecordingReminderService reminders,
        RecordingTodoService todos,
        SeriesScope editScope = SeriesScope.OccurrenceOnly,
        bool recurring = false,
        Func<CancellationToken, Task<SeriesScope?>>? selectConversionScope = null,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        var row = TestData.Row("会议", "2026-08-03T10:30:00+08:00") with
        {
            RecurrenceText = recurring ? "每天" : null
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

        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));

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
        public Exception? ConversionFailure { get; init; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
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
            ReminderConversions.Add((occurrenceId, draft, scope));
            return Task.CompletedTask;
        }
    }
}
