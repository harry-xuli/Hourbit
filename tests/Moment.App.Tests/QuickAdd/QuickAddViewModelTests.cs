using Moment.App.QuickAdd;
using Moment.App.Timeline;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;
using System.Globalization;

namespace Moment.App.Tests.QuickAdd;

public sealed class QuickAddViewModelTests
{
    [Fact]
    public async Task Enter_does_not_create_when_parser_returns_ambiguity()
    {
        var service = new RecordingReminderService();
        var vm = Create(
            new ParseResult.Ambiguous("晚上提醒我看书",
                [new("今天 20:00", TestData.Draft("看书", "2026-07-30T20:00:00+08:00"))]),
            service);
        vm.Text = "晚上提醒我看书";

        await vm.SubmitAsync();

        Assert.True(vm.IsChoicePanelVisible);
        Assert.Equal("请选择具体时间", vm.GuidanceText);
        Assert.Empty(service.Created);
    }

    [Fact]
    public async Task Choosing_ambiguity_option_shows_absolute_preview_then_Enter_creates()
    {
        var service = new RecordingReminderService();
        var draft = TestData.Draft("看书", "2026-07-30T09:00:00+08:00");
        var vm = Create(new ParseResult.Ambiguous(
            "晚上提醒我看书", [new("今天 20:00", draft)]), service);
        vm.Text = "晚上提醒我看书";
        await vm.SubmitAsync();

        await vm.ChooseAsync(vm.Choices[0]);

        Assert.False(vm.IsChoicePanelVisible);
        Assert.Equal("提醒 · 2026-07-30 09:00", vm.PreviewText);
        Assert.Empty(service.Created);

        await vm.SubmitAsync();

        Assert.Equal(draft, Assert.Single(service.Created));
    }

    [Theory]
    [MemberData(nameof(TypeSpecificDrafts))]
    public async Task Successful_draft_shows_type_specific_preview_and_dispatches_to_its_service(
        ItemDraft draft,
        string expectedPreview,
        bool createsReminder)
    {
        var reminders = new RecordingReminderService();
        var todos = new RecordingTodoService();
        var vm = Create(new ParseResult.Success(draft), reminders, todos);
        vm.Text = "输入保留到创建完成";

        Assert.Equal(expectedPreview, vm.PreviewText);

        await vm.SubmitAsync();

        if (createsReminder)
        {
            Assert.Equal(draft, Assert.Single(reminders.Created));
            Assert.Empty(todos.Created);
        }
        else
        {
            Assert.Equal(draft, Assert.Single(todos.Created));
            Assert.Empty(reminders.Created);
        }
    }

    public static TheoryData<ItemDraft, string, bool> TypeSpecificDrafts => new()
    {
        {
            new TodoDraft("整理房间", null, ReminderImportance.Normal),
            "待办 · 无日期",
            false
        },
        {
            new TodoDraft("提交报告", new DateOnly(2026, 8, 5), ReminderImportance.Important),
            "待办 · 截止 2026-08-05",
            false
        },
        {
            TestData.Draft("开会", "2026-08-05T14:30:00+08:00"),
            "提醒 · 2026-08-05 14:30",
            true
        }
    };

    [Fact]
    public void Parse_receives_the_injected_active_Windows_culture()
    {
        var parser = new CapturingParser(new ParseResult.Success(
            new TodoDraft("提交报告", new DateOnly(2026, 8, 5), ReminderImportance.Normal)));
        var culture = CultureInfo.GetCultureInfo("en-GB");
        var vm = Create(parser, new RecordingReminderService(), new RecordingTodoService(), culture);

        vm.Text = "submit report 05/08/2026";

        Assert.Same(culture, parser.ReceivedCulture);
    }

    [Fact]
    public async Task Todo_persistence_failure_preserves_input_and_shows_actionable_error()
    {
        var todos = new RecordingTodoService
        {
            CreateFailure = new InvalidOperationException("无法保存待办，请重试。")
        };
        var vm = Create(
            new ParseResult.Success(new TodoDraft("提交报告", null, ReminderImportance.Normal)),
            new RecordingReminderService(),
            todos);
        vm.Text = "提交报告";
        var hides = 0;
        vm.HideRequested += (_, _) => hides++;

        await vm.SubmitAsync();

        Assert.Equal("提交报告", vm.Text);
        Assert.Equal("无法保存待办，请重试。", vm.ErrorMessage);
        Assert.Equal(0, hides);
        Assert.Empty(todos.Created);
    }

    [Fact]
    public async Task Invalid_parse_preserves_input_and_exposes_parser_message()
    {
        var service = new RecordingReminderService();
        var vm = Create(new ParseResult.Invalid("内容", "未找到明确的提醒时间。"), service);
        vm.Text = "内容";

        await vm.SubmitAsync();

        Assert.Equal("内容", vm.Text);
        Assert.Equal("未找到明确的提醒时间。", vm.ErrorMessage);
        Assert.False(vm.IsChoicePanelVisible);
        Assert.Empty(service.Created);
    }

    [Fact]
    public async Task Escape_requests_hide_without_clearing_input_and_Tab_expands_details()
    {
        var vm = Create(new ParseResult.Invalid("", "无效"), new RecordingReminderService());
        vm.Text = "晚上提醒我看书";
        var hides = 0;
        vm.HideRequested += (_, _) => hides++;

        await vm.HideCommand.ExecuteAsync(null);
        await vm.ToggleDetailsCommand.ExecuteAsync(null);

        Assert.Equal(1, hides);
        Assert.Equal("晚上提醒我看书", vm.Text);
        Assert.True(vm.AreDetailsVisible);
    }

    [Fact]
    public async Task Expanded_fields_show_parsed_values_and_create_the_user_modified_draft()
    {
        var service = new RecordingReminderService();
        var vm = Create(new ParseResult.Success(
            TestData.Draft("看书", "2026-07-30T09:00:00+08:00")), service);
        vm.Text = "明早9点看书";

        await vm.ToggleDetailsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Details);
        Assert.Equal("看书", vm.Details.Title);
        Assert.Equal("2026-07-30", vm.Details.DateText);
        Assert.Equal("09:00", vm.Details.TimeText);
        Assert.Equal(ReminderKind.Countdown, vm.Details.SelectedKind);
        Assert.Equal(ReminderImportance.Normal, vm.Details.SelectedImportance);
        Assert.Equal(EditRecurrenceMode.None, vm.Details.SelectedRecurrence);

        vm.Details.Title = "晨间阅读";
        vm.Details.TimeText = "09:30";
        vm.Details.SelectedKind = ReminderKind.Plan;
        vm.Details.SelectedImportance = ReminderImportance.Important;
        vm.Details.SelectedRecurrence = EditRecurrenceMode.Daily;

        await vm.SubmitAsync();

        var created = Assert.Single(service.Created);
        Assert.Equal("晨间阅读", created.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T09:30:00+08:00"), created.DueAt);
        Assert.Equal(ReminderKind.Plan, created.Kind);
        Assert.Equal(ReminderImportance.Important, created.Importance);
        Assert.Equal(RecurrenceKind.Daily, created.Recurrence?.Kind);
    }

    [Fact]
    public async Task Submit_command_rejects_reentrancy_during_create()
    {
        var service = new RecordingReminderService(blockCreate: true);
        var vm = Create(new ParseResult.Success(
            TestData.Draft("看书", "2026-07-30T09:00:00+08:00")), service);
        vm.Text = "明早9点看书";

        var first = vm.SubmitCommand.ExecuteAsync(null);
        await service.CreateEntered.Task;
        var second = vm.SubmitCommand.ExecuteAsync(null);
        service.ReleaseCreate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(service.Created);
        Assert.False(vm.SubmitCommand.IsRunning);
    }

    private static QuickAddViewModel Create(
        ParseResult result,
        RecordingReminderService service,
        RecordingTodoService? todos = null) =>
        Create(new StubParser(result), service, todos ?? new RecordingTodoService(),
            CultureInfo.GetCultureInfo("zh-CN"));

    private static QuickAddViewModel Create(
        IChineseTimeParser parser,
        RecordingReminderService reminders,
        RecordingTodoService todos,
        CultureInfo culture) =>
        new(parser, reminders, todos,
            new FakeClock("2026-07-29T09:00:00+08:00"),
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-quick", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            culture);

    private sealed class StubParser(ParseResult result) : IChineseTimeParser
    {
        public ParseResult Parse(
            string text,
            DateTimeOffset now,
            TimeZoneInfo zone,
            System.Globalization.CultureInfo culture) => result;
    }

    private sealed class CapturingParser(ParseResult result) : IChineseTimeParser
    {
        public CultureInfo? ReceivedCulture { get; private set; }

        public ParseResult Parse(
            string text,
            DateTimeOffset now,
            TimeZoneInfo zone,
            CultureInfo culture)
        {
            ReceivedCulture = culture;
            return result;
        }
    }

    private sealed class RecordingReminderService(bool blockCreate = false) : IReminderService
    {
        public List<ReminderDraft> Created { get; } = [];
        public TaskCompletionSource CreateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            CreateEntered.TrySetResult();
            if (blockCreate)
                await ReleaseCreate.Task.WaitAsync(ct);
            return ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt);
        }
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingTodoService : ITodoService
    {
        public List<TodoDraft> Created { get; } = [];
        public Exception? CreateFailure { get; init; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct)
        {
            if (CreateFailure is not null)
                throw CreateFailure;
            Created.Add(draft);
            return Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title,
                DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"),
                draft.DueDate, draft.Importance, false, null));
        }

        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CompleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToReminderAsync(
            Guid todoId, ReminderDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
