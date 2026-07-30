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
        IReminderActionService? actions = null) =>
        new(query, new FakeClock("2026-07-29T09:00:00+08:00"),
            service ?? new RecordingReminderService(),
            actions ?? new BlockingActionService(completesImmediately: true),
            dialogs ?? new Dialogs(),
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-vm", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));

    private sealed class FakeTimelineQuery(params TimelineRow[] rows) : ITimelineQuery
    {
        public Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TimelineRow>>(rows);
    }

    private sealed class ThrowingTimelineQuery(Exception exception) : ITimelineQuery
    {
        public Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
            DateOnly localDate, TimeZoneInfo zone, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<TimelineRow>>(exception);
    }

    private sealed class CancelThenReturnQuery(TimelineRow row) : ITimelineQuery
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstCancellationObserved { get; private set; }

        public async Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
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
            return [row];
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
        public List<(Guid Id, SeriesScope Scope)> Edited { get; } = [];
        public List<(Guid Id, SeriesScope Scope)> Deleted { get; } = [];
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct)
        {
            Edited.Add((occurrenceId, scope));
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct)
        {
            Deleted.Add((occurrenceId, scope));
            return Task.CompletedTask;
        }
    }

    private sealed class Dialogs : ITimelineDialogService
    {
        public SeriesScope? EditScope { get; set; } = SeriesScope.OccurrenceOnly;
        public SeriesScope? DeleteScope { get; set; } = SeriesScope.OccurrenceOnly;
        public int EditFormCalls { get; private set; }
        public Exception? QuickAddFailure { get; set; }
        public Task<SeriesScope?> SelectEditScopeAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(EditScope);
        public Task<SeriesScope?> SelectDeleteScopeAsync(TimelineItemViewModel item, CancellationToken ct) =>
            Task.FromResult(DeleteScope);
        public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct)
        {
            EditFormCalls++;
            return Task.FromResult<ReminderDraft?>(new(
                item.Title, item.DueAt, item.Kind, item.Importance, null));
        }
        public void OpenQuickAdd()
        {
            if (QuickAddFailure is not null)
                throw QuickAddFailure;
        }
    }
}
