using Moment.Core.Domain;
using Moment.Core.Scheduling;
using Moment.TestSupport;

namespace Moment.Core.Tests.Scheduling;

public sealed class RecoveryClassifierTests
{
    [Theory]
    [InlineData(4, 59, true, false)]
    [InlineData(5, 0, true, false)]
    [InlineData(5, 1, false, true)]
    public void Normal_reminders_at_the_five_minute_boundary_are_classified_correctly(
        int minutes, int seconds, bool immediate, bool summary)
    {
        var now = DateTimeOffset.Parse("2026-07-29T09:00:00+08:00");
        var reminder = TestData.Scheduled("normal", now.AddMinutes(-minutes).AddSeconds(-seconds).ToString("O"));

        var result = new RecoveryClassifier().Classify([reminder], now);

        Assert.Equal(immediate, result.Immediate.Contains(reminder));
        Assert.Equal(summary, result.Summary.Contains(reminder));
    }

    [Fact]
    public void Important_reminders_are_immediate_even_when_late()
    {
        var now = DateTimeOffset.Parse("2026-07-29T09:00:00+08:00");
        var reminder = TestData.Scheduled("important", now.AddHours(-1).ToString("O"), ReminderImportance.Important);

        var result = new RecoveryClassifier().Classify([reminder], now);

        Assert.Equal([reminder], result.Immediate);
        Assert.Empty(result.Summary);
    }
}
