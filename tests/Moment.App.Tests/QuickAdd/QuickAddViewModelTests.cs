using Moment.App.QuickAdd;
using Moment.App.Timeline;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

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
        Assert.Equal("2026年7月30日 09:00 · 单次 · 普通提醒", vm.PreviewText);
        Assert.Empty(service.Created);

        await vm.SubmitAsync();

        Assert.Equal(draft, Assert.Single(service.Created));
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

    private static QuickAddViewModel Create(ParseResult result, RecordingReminderService service) =>
        new(new StubParser(result), service,
            new FakeClock("2026-07-29T09:00:00+08:00"),
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-quick", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));

    private sealed class StubParser(ParseResult result) : IChineseTimeParser
    {
        public ParseResult Parse(
            string text,
            DateTimeOffset now,
            TimeZoneInfo zone,
            System.Globalization.CultureInfo culture) => result;
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
}
