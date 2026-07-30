using Moment.Core.Domain;
using Moment.Core.Services;
using Moment.Infrastructure.Data;
using Moment.TestSupport;
using System.IO;

namespace Moment.App.Tests.Data;

public sealed class SqliteTimelineQueryTests
{
    [Fact]
    public async Task Query_uses_inclusive_start_and_exclusive_end_for_offset_local_date()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-test", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        var item = ReminderItem.Create("边界", ReminderKind.Plan, ReminderImportance.Normal,
            DateTimeOffset.Parse("2026-07-29T00:00:00+08:00"),
            DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));
        var atStart = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"));
        var atEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-07-31T00:00:00+08:00"));
        await repository.SaveItemWithOccurrenceAsync(item, atStart, CancellationToken.None);
        await repository.SaveOccurrenceAsync(atEnd, CancellationToken.None);

        var rows = await new SqliteTimelineQuery(path).GetTimelineAsync(
            new DateOnly(2026, 7, 30), zone, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(atStart.Id, row.OccurrenceId);
        Assert.Equal(TimeSpan.FromHours(8), row.DueAt.Offset);
    }

    [Fact]
    public async Task Query_computes_a_23_hour_utc_window_across_spring_DST()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var zone = CreateEasternTestZone();
        var item = ReminderItem.Create("夏令时", ReminderKind.Alarm, ReminderImportance.Important,
            DateTimeOffset.Parse("2026-03-07T00:00:00-05:00"),
            DateTimeOffset.Parse("2026-03-08T00:00:00-05:00"));
        var atStart = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-08T00:00:00-05:00"));
        var beforeEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-08T23:59:59-04:00"));
        var atEnd = ReminderOccurrence.Schedule(item.Id, DateTimeOffset.Parse("2026-03-09T00:00:00-04:00"));
        await repository.SaveItemWithOccurrenceAsync(item, atStart, CancellationToken.None);
        await repository.SaveOccurrenceAsync(beforeEnd, CancellationToken.None);
        await repository.SaveOccurrenceAsync(atEnd, CancellationToken.None);

        var rows = await new SqliteTimelineQuery(path).GetTimelineAsync(
            new DateOnly(2026, 3, 8), zone, CancellationToken.None);

        Assert.Equal([atStart.Id, beforeEnd.Id], rows.Select(row => row.OccurrenceId));
    }

    private static TimeZoneInfo CreateEasternTestZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), daylightStart, daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Eastern-test", TimeSpan.FromHours(-5), "Eastern-test",
            "Eastern-test", "Eastern-test DST", [rule]);
    }
}
