using Hourbit.App.Timeline;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Analytics;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;
using Hourbit.TestSupport;
using System.Globalization;

namespace Hourbit.App.Tests.Composition;

public sealed class QuickAddTimelineCompositionTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-composition", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    [Fact]
    public async Task Successful_create_awaits_timeline_refresh_before_hiding_and_updates_header()
    {
        var query = new RefreshQuery(TestData.Row(
            "项目复盘", "2026-07-29T10:00:00+08:00"));
        var service = new ReminderServiceStub();
        var timeline = CreateTimeline(query, service);
        await timeline.LoadAsync();
        var quickAdd = CompositionRoot.ComposeQuickAdd(
            new ParserStub(new ParseResult.Success(
                TestData.Draft("项目复盘", "2026-07-29T10:00:00+08:00"))),
            service, new TodoServiceStub(),
            new FakeClock("2026-07-29T09:00:00+08:00"), Zone,
            CultureInfo.GetCultureInfo("zh-CN"), timeline);
        quickAdd.Text = "上午10点项目复盘";
        var hides = 0;
        string? titleObservedAtHide = null;
        quickAdd.HideRequested += (_, _) =>
        {
            hides++;
            titleObservedAtHide = timeline.Items.Single().Title;
        };

        var submit = quickAdd.SubmitAsync();
        await query.RefreshEntered.Task;

        Assert.False(submit.IsCompleted);
        Assert.Equal(0, hides);
        query.ReleaseRefresh.SetResult();
        await submit;

        Assert.Equal(1, hides);
        Assert.Equal("项目复盘", titleObservedAtHide);
        Assert.Equal("10:00 项目复盘", timeline.NextReminderText);
        Assert.Null(quickAdd.ErrorMessage);
    }

    [Fact]
    public async Task Refresh_failure_after_persistence_is_visible_and_keeps_quick_add_open()
    {
        var query = new RefreshQuery(
            TestData.Row("项目复盘", "2026-07-29T10:00:00+08:00"))
        {
            RefreshFailure = new InvalidOperationException("时间轴刷新失败")
        };
        query.ReleaseRefresh.SetResult();
        var service = new ReminderServiceStub();
        var timeline = CreateTimeline(query, service);
        await timeline.LoadAsync();
        var quickAdd = CompositionRoot.ComposeQuickAdd(
            new ParserStub(new ParseResult.Success(
                TestData.Draft("项目复盘", "2026-07-29T10:00:00+08:00"))),
            service, new TodoServiceStub(),
            new FakeClock("2026-07-29T09:00:00+08:00"), Zone,
            CultureInfo.GetCultureInfo("zh-CN"), timeline);
        quickAdd.Text = "上午10点项目复盘";
        var hides = 0;
        quickAdd.HideRequested += (_, _) => hides++;

        await quickAdd.SubmitAsync();

        Assert.Single(service.Created);
        Assert.Equal(0, hides);
        Assert.Equal("时间轴刷新失败", timeline.ErrorMessage);
        Assert.Contains("提醒已创建", quickAdd.ErrorMessage);
        Assert.Contains("时间轴刷新失败", quickAdd.ErrorMessage);
        Assert.True(quickAdd.IsRefreshOnly);
        Assert.False(quickAdd.CanEdit);

        query.RefreshFailure = null;
        await quickAdd.SubmitAsync();

        Assert.Single(service.Created);
        Assert.Equal(1, hides);
        Assert.Equal("项目复盘", Assert.Single(timeline.Items).Title);
        Assert.Null(quickAdd.ErrorMessage);
    }

    private static TimelineViewModel CreateTimeline(
        ITimelineQuery query, IReminderService reminders) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"), reminders,
            new ActionServiceStub(), new TodoServiceStub(),
            new DialogStub(), new DialogStub(), Zone);

    private sealed class RefreshQuery(TimelineRow refreshedRow) : ITimelineQuery
    {
        private int _calls;
        public TaskCompletionSource RefreshEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? RefreshFailure { get; set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            LocalDateRange range, DateTimeOffset now,
            TimeZoneInfo zone, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return new TimelineSnapshot([], [], 0, 0);
            RefreshEntered.TrySetResult();
            await ReleaseRefresh.Task.WaitAsync(ct);
            if (RefreshFailure is not null)
                throw RefreshFailure;
            return new TimelineSnapshot([], [refreshedRow], 0, 0);
        }
    }

    private sealed class ParserStub(ParseResult result) : IChineseTimeParser
    {
        public ParseResult Parse(
            string text,
            DateTimeOffset now,
            TimeZoneInfo zone,
            System.Globalization.CultureInfo culture) => result;
    }

    private sealed class ReminderServiceStub : IReminderService
    {
        public List<ReminderDraft> Created { get; } = [];
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct)
        {
            Created.Add(draft);
            return Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        }
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ActionServiceStub : IReminderActionService
    {
        public Task CompleteAsync(Guid occurrenceId, CancellationToken ct) => Task.CompletedTask;
        public Task IgnoreAsync(Guid occurrenceId, CancellationToken ct) => Task.CompletedTask;
        public Task<ReminderOccurrence> SnoozeAsync(
            Guid occurrenceId, TimeSpan delay, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    private sealed class TodoServiceStub : ITodoService
    {
        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title, DateTimeOffset.UtcNow,
                draft.DueDate, draft.Importance, false, null));
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

    private sealed class DialogStub : ITimelineDialogService, ITodoDialogService
    {
        public Task<SeriesScope?> SelectEditScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<SeriesScope?> SelectDeleteScopeAsync(
            TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<SeriesScope?>(null);
        public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult<ReminderDraft?>(null);
        public Task<TodoDialogResult> EditTodoAsync(
            TodoItem item,
            CancellationToken ct) =>
            Task.FromResult(new TodoDialogResult(false));
        public void OpenQuickAdd() { }
    }
}
