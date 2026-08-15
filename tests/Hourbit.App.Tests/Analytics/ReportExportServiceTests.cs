using System.Text;
using Hourbit.App.Analytics;
using Hourbit.Core.Analytics;
using Hourbit.Core.Reporting;
using Hourbit.TestSupport;

namespace Hourbit.App.Tests.Analytics;

public sealed class ReportExportServiceTests
{
    [Fact]
    public async Task Export_writes_both_pdf_and_csv_from_the_same_snapshot()
    {
        using var temp = new TempDirectory();
        var basePath = Path.Combine(temp.Path, "report");
        var service = new ReportExportService("Hourbit 日程", "0.6.0");

        var paths = await service.ExportAsync(
            CreateSnapshot(), ReportPrivacyMode.Full, basePath, default);

        Assert.Equal([basePath + ".pdf", basePath + ".csv"], paths);
        Assert.True(File.Exists(basePath + ".pdf"));
        Assert.True(File.Exists(basePath + ".csv"));
        Assert.StartsWith(
            "%PDF-1.4",
            Encoding.ASCII.GetString(await File.ReadAllBytesAsync(basePath + ".pdf")));
        var csv = await File.ReadAllTextAsync(basePath + ".csv");
        Assert.Contains("SnapshotId", csv);
    }

    [Fact]
    public async Task Export_failure_leaves_no_partial_output()
    {
        using var temp = new TempDirectory();
        var basePath = Path.Combine(temp.Path, "missing", "report");
        var service = new ReportExportService("Hourbit 日程", "0.6.0");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.ExportAsync(
                CreateSnapshot(), ReportPrivacyMode.Full, basePath, default));

        Assert.False(File.Exists(basePath + ".pdf"));
        Assert.False(File.Exists(basePath + ".csv"));
    }

    private static AnalyticsSnapshot CreateSnapshot() =>
        new(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.FromHours(8)),
            new LocalDateRange(new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 15)),
            "China Standard Time",
            new AnalyticsTotals(7, 4, 3, 1, 0, 5, 2, 1),
            [new DistributionSlice("completed", "已完成", 4)],
            [new DistributionSlice("todo", "待办", 5)],
            [new DistributionSlice("normal", "普通", 5)],
            [new TrendBucket(new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 9), "08-09", 1)],
            []);
}
