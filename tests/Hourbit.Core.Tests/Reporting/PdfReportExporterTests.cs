using System.Text;
using Hourbit.Core.Analytics;
using Hourbit.Core.Reporting;

namespace Hourbit.Core.Tests.Reporting;

public sealed class PdfReportExporterTests
{
    [Fact]
    public async Task Writes_a_well_formed_pdf_with_chinese_text()
    {
        var snapshot = CreateSnapshot();
        using var stream = new MemoryStream();

        await PdfReportExporter.WriteAsync(
            snapshot, ReportPrivacyMode.Full, "Hourbit 日程", "0.6.0", stream, default);

        var bytes = stream.ToArray();
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("startxref", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("STSong-Light", text);
        // "分析报告" as UTF-16BE hex: 分=5206 析=6790 报=62A5 告=544A
        Assert.Contains("5206679062A5544A", text);
    }

    [Fact]
    public async Task Anonymous_report_omits_identifying_headers_and_notes_it()
    {
        var snapshot = CreateSnapshot();
        using var stream = new MemoryStream();

        await PdfReportExporter.WriteAsync(
            snapshot, ReportPrivacyMode.Anonymous, "Hourbit 日程", "0.6.0", stream, default);

        var text = Encoding.ASCII.GetString(stream.ToArray());
        // "匿名统计" as UTF-16BE hex: 匿=533F 名=540D 统=7EDF 计=8BA1
        Assert.Contains("533F540D7EDF8BA1", text);
    }

    [Fact]
    public async Task Rejects_an_unknown_privacy_mode()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PdfReportExporter.WriteAsync(
                CreateSnapshot(), (ReportPrivacyMode)99,
                "Hourbit 日程", "0.6.0", stream, default));
    }

    private static AnalyticsSnapshot CreateSnapshot() =>
        new(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.FromHours(8)),
            new LocalDateRange(new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 15)),
            "China Standard Time",
            new AnalyticsTotals(7, 4, 3, 1, 0, 5, 2, 1),
            [new DistributionSlice("completed", "已完成", 4),
             new DistributionSlice("incomplete", "未完成", 2),
             new DistributionSlice("overdue", "已逾期", 1)],
            [new DistributionSlice("todo", "待办", 5),
             new DistributionSlice("reminder", "提醒", 2)],
            [new DistributionSlice("normal", "普通", 5),
             new DistributionSlice("important", "重要", 2)],
            [new TrendBucket(new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 9), "08-09", 1),
             new TrendBucket(new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 15), "08-15", 4)],
            []);
}
