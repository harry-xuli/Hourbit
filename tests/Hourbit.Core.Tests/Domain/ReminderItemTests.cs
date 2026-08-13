using Hourbit.Core.Domain;

namespace Hourbit.Core.Tests.Domain;

public sealed class ReminderItemTests
{
    [Fact]
    public void Create_trims_title_and_rejects_due_time_before_creation()
    {
        var created = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
        var due = created.AddMinutes(20);

        var item = ReminderItem.Create("  起来活动  ", ReminderKind.Countdown,
            ReminderImportance.Normal, created, due);

        Assert.Equal("起来活动", item.Title);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReminderItem.Create("错误", ReminderKind.Alarm,
                ReminderImportance.Normal, created, created.AddSeconds(-1)));
    }
}
