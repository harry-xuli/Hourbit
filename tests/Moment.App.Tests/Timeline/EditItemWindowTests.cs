using System.Windows.Automation;
using System.Windows.Controls;
using Moment.App.Timeline;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.Timeline;

public sealed class EditItemWindowTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-edit-window", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    [Fact]
    public Task Todo_window_exposes_accessible_fields_and_closes_only_after_success() =>
        WpfTestHost.RunAsync(async () =>
        {
            var service = new TodoServiceStub();
            var vm = new EditTodoViewModel(CreateTodo(), Zone, service);
            var window = new EditTodoWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            var date = Assert.IsType<TextBox>(window.FindName("TodoDateBox"));
            var time = Assert.IsType<TextBox>(window.FindName("TodoTimeBox"));
            Assert.Equal("待办日期，格式年-月-日，可留空", AutomationProperties.GetName(date));
            Assert.Equal("提醒时间，格式小时:分钟，可留空", AutomationProperties.GetName(time));

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.False(window.IsVisible);
        });

    [Fact]
    public Task Reminder_window_stays_open_when_persistence_fails() =>
        WpfTestHost.RunAsync(async () =>
        {
            var item = new TimelineItemViewModel(
                TestData.Row("会议", "2026-08-03T10:30:00+08:00"),
                DateTimeOffset.Parse("2026-08-03T09:00:00+08:00"));
            var vm = new EditReminderViewModel(
                item,
                Zone,
                new FailingReminderService(),
                new TodoServiceStub(),
                SeriesScope.OccurrenceOnly);
            var window = new EditReminderWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.True(window.IsVisible);
            Assert.Equal("保存失败，请重试。", vm.ErrorMessage);
            window.Close();
        });

    private static TodoItem CreateTodo() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000006"),
        "提交报告",
        DateTimeOffset.Parse("2026-08-01T09:00:00+08:00"),
        new DateOnly(2026, 8, 5),
        ReminderImportance.Normal,
        false,
        null);

    private sealed class FailingReminderService : IReminderService
    {
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task EditAsync(
            Guid occurrenceId,
            ReminderDraft draft,
            SeriesScope scope,
            CancellationToken ct) =>
            throw new InvalidOperationException("保存失败，请重试。");
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TodoServiceStub : ITodoService
    {
        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
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
