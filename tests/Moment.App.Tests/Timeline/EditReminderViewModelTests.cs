using Moment.App.Timeline;
using Moment.Core.Domain;
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

    private static EditReminderViewModel Create()
    {
        var item = new TimelineItemViewModel(
            TestData.Row("会议", "2026-07-29T10:30:00+08:00"),
            DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"));
        return new EditReminderViewModel(item, Zone);
    }
}
