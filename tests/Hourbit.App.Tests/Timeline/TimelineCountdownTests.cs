using Hourbit.App.Timeline;
using Hourbit.Core.Domain;
using Hourbit.Core.Services;

namespace Hourbit.App.Tests.Timeline;

public sealed class TimelineCountdownTests
{
    [Theory]
    [InlineData("2026-08-12T15:55:01+08:00", "剩余 04:59")]
    [InlineData("2026-08-12T14:55:01+08:00", "剩余 1:04:59")]
    [InlineData("2026-08-12T16:00:00+08:00", "已到时")]
    [InlineData("2026-08-12T16:01:00+08:00", "已到时")]
    public void Countdown_remaining_text_is_recomputed_from_absolute_clock(
        string nowText, string expected)
    {
        var row = new TimelineRow(
            Guid.NewGuid(), "倒计时", DateTimeOffset.Parse("2026-08-12T16:00:00+08:00"),
            ReminderKind.Countdown, ReminderImportance.Normal,
            OccurrenceState.Scheduled, null);
        var vm = new TimelineItemViewModel(row, DateTimeOffset.Parse(nowText));

        Assert.True(vm.IsCountdown);
        Assert.Equal(expected, vm.RemainingText);
    }

    [Fact]
    public void Ordinary_reminder_has_no_remaining_text()
    {
        var row = new TimelineRow(
            Guid.NewGuid(), "开会", DateTimeOffset.Parse("2026-08-12T16:00:00+08:00"),
            ReminderKind.Plan, ReminderImportance.Normal,
            OccurrenceState.Scheduled, null);
        var vm = new TimelineItemViewModel(
            row, DateTimeOffset.Parse("2026-08-12T15:55:01+08:00"));

        Assert.False(vm.IsCountdown);
        Assert.Equal("", vm.RemainingText);
    }

    [Theory]
    [InlineData(OccurrenceState.Completed)]
    [InlineData(OccurrenceState.Ignored)]
    public void Terminal_countdown_has_no_remaining_text(OccurrenceState state)
    {
        var row = new TimelineRow(
            Guid.NewGuid(), "已处理倒计时",
            DateTimeOffset.Parse("2026-08-21T16:00:00+08:00"),
            ReminderKind.Countdown, ReminderImportance.Normal,
            state, null);
        var vm = new TimelineItemViewModel(
            row, DateTimeOffset.Parse("2026-08-21T15:55:00+08:00"));

        Assert.Equal(string.Empty, vm.RemainingText);
    }
}
