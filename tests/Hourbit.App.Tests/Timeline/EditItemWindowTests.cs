using System.Windows.Automation;
using System.Windows.Controls;
using Hourbit.App.Timeline;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Timeline;

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

    [Fact]
    public Task Todo_conversion_refresh_failure_disables_fields_and_labels_retry() =>
        WpfTestHost.RunAsync(async () =>
        {
            var refreshAttempts = 0;
            var service = new TodoServiceStub();
            var vm = new EditTodoViewModel(
                CreateTodo(),
                Zone,
                service,
                _ => ++refreshAttempts == 1
                    ? throw new InvalidOperationException("时间轴刷新失败")
                    : Task.CompletedTask);
            vm.TimeText = "14:30";
            var window = new EditTodoWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            await vm.SaveCommand.ExecuteAsync(null);
            window.UpdateLayout();

            var date = Assert.IsType<TextBox>(window.FindName("TodoDateBox"));
            var save = Assert.IsType<Button>(window.FindName("SaveTodoButton"));
            Assert.False(date.IsEnabled);
            Assert.Equal("重试刷新", save.Content);
            Assert.True(window.IsVisible);

            window.Close();

            Assert.True(window.IsVisible);
            Assert.True(vm.IsRefreshOnly);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(1, service.TodoToReminderConversions);
            Assert.False(window.IsVisible);
        });

    [Fact]
    public Task Reminder_conversion_refresh_failure_disables_fields_and_labels_retry() =>
        WpfTestHost.RunAsync(async () =>
        {
            var refreshAttempts = 0;
            var service = new TodoServiceStub();
            var item = new TimelineItemViewModel(
                TestData.Row("会议", "2026-08-03T10:30:00+08:00"),
                DateTimeOffset.Parse("2026-08-03T09:00:00+08:00"));
            var vm = new EditReminderViewModel(
                item,
                Zone,
                new ReminderServiceStub(),
                service,
                SeriesScope.OccurrenceOnly,
                afterSaved: _ => ++refreshAttempts == 1
                    ? throw new InvalidOperationException("时间轴刷新失败")
                    : Task.CompletedTask);
            vm.TimeText = "";
            var window = new EditReminderWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            await vm.SaveCommand.ExecuteAsync(null);
            window.UpdateLayout();

            var date = Assert.IsType<TextBox>(window.FindName("ReminderDateBox"));
            var save = Assert.IsType<Button>(window.FindName("SaveReminderButton"));
            Assert.False(date.IsEnabled);
            Assert.Equal("重试刷新", save.Content);
            Assert.True(window.IsVisible);

            window.Close();

            Assert.True(window.IsVisible);
            Assert.True(vm.IsRefreshOnly);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(1, service.ReminderToTodoConversions);
            Assert.False(window.IsVisible);
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

    private sealed class ReminderServiceStub : IReminderService
    {
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task EditAsync(
            Guid occurrenceId,
            ReminderDraft draft,
            SeriesScope scope,
            CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class TodoServiceStub : ITodoService
    {
        public int TodoToReminderConversions { get; private set; }
        public int ReminderToTodoConversions { get; private set; }

        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CompleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToReminderAsync(
            Guid todoId, ReminderDraft draft, CancellationToken ct)
        {
            TodoToReminderConversions++;
            return Task.CompletedTask;
        }
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, SeriesScope scope, CancellationToken ct)
        {
            ReminderToTodoConversions++;
            return Task.CompletedTask;
        }
    }
}
