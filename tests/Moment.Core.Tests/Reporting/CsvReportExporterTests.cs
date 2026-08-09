using System.Globalization;
using System.Text;
using Moment.Core.Analytics;
using Moment.Core.Domain;
using Moment.Core.Reporting;

namespace Moment.Core.Tests.Reporting;

public sealed class CsvReportExporterTests
{
    [Fact]
    public async Task Full_export_writes_exact_UTF8_BOM_CRLF_columns_escaping_and_deleted_rows()
    {
        var snapshot = Snapshot(
            new AnalyticsDetailRow(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                AnalyticsItemType.Reminder,
                "复盘, \"关键\"\r\n下一行",
                ReminderKind.Plan,
                OccurrenceState.Completed,
                ReminderImportance.Important,
                Parse("2026-07-01T08:09:10.1234567+08:00"),
                null,
                Parse("2026-08-01T09:10:11.7654321+05:30"),
                Parse("2026-08-01T03:40:12.0000000Z"),
                null,
                AnalyticsRecordStatus.Completed),
            new AnalyticsDetailRow(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                AnalyticsItemType.Todo,
                "已删除待办",
                null,
                null,
                ReminderImportance.Normal,
                Parse("2026-07-02T01:02:03.0000000Z"),
                new DateOnly(2026, 8, 9),
                null,
                null,
                Parse("2026-08-08T07:06:05.0000000-04:00"),
                AnalyticsRecordStatus.Deleted));
        await using var destination = new MemoryStream();
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

        try
        {
            await CsvReportExporter.WriteAsync(
                snapshot, ReportPrivacyMode.Full, destination, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        const string expectedText =
            "RecordId,ItemType,Title,Importance,CreatedAt,DueDate,DueAt,CompletedAt,DeletedAt,Status\r\n" +
            "10000000-0000-0000-0000-000000000001,Reminder,\"复盘, \"\"关键\"\"\r\n下一行\",Important,2026-07-01T08:09:10.1234567+08:00,,2026-08-01T09:10:11.7654321+05:30,2026-08-01T03:40:12.0000000+00:00,,Completed\r\n" +
            "10000000-0000-0000-0000-000000000002,Todo,已删除待办,Normal,2026-07-02T01:02:03.0000000+00:00,2026-08-09,,,2026-08-08T07:06:05.0000000-04:00,Deleted\r\n";

        Assert.Equal(Utf8BomBytes(expectedText), destination.ToArray());
    }

    [Fact]
    public async Task Anonymous_export_omits_only_stable_id_and_title_columns()
    {
        var snapshot = Snapshot(new AnalyticsDetailRow(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            AnalyticsItemType.Reminder,
            "不得导出",
            ReminderKind.Alarm,
            OccurrenceState.Scheduled,
            ReminderImportance.Important,
            Parse("2026-08-01T01:02:03.0000000Z"),
            null,
            Parse("2026-08-02T04:05:06.0000000+08:00"),
            null,
            null,
            AnalyticsRecordStatus.Incomplete));
        await using var destination = new MemoryStream();

        await CsvReportExporter.WriteAsync(
            snapshot, ReportPrivacyMode.Anonymous, destination, CancellationToken.None);

        const string expectedText =
            "ItemType,Importance,CreatedAt,DueDate,DueAt,CompletedAt,DeletedAt,Status\r\n" +
            "Reminder,Important,2026-08-01T01:02:03.0000000+00:00,,2026-08-02T04:05:06.0000000+08:00,,,Incomplete\r\n";
        var bytes = destination.ToArray();

        Assert.Equal(Utf8BomBytes(expectedText), bytes);
        var text = Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.GetPreamble().Length));
        Assert.DoesNotContain("不得导出", text, StringComparison.Ordinal);
        Assert.DoesNotContain("30000000-0000-0000-0000-000000000001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_export_writes_BOM_and_header_only()
    {
        await using var destination = new MemoryStream();

        await CsvReportExporter.WriteAsync(
            Snapshot(), ReportPrivacyMode.Full, destination, CancellationToken.None);

        Assert.Equal(
            Utf8BomBytes(
                "RecordId,ItemType,Title,Importance,CreatedAt,DueDate,DueAt,CompletedAt,DeletedAt,Status\r\n"),
            destination.ToArray());
    }

    [Fact]
    public async Task Pre_cancelled_export_leaves_existing_destination_unchanged()
    {
        byte[] original = [0x01, 0x02, 0x03, 0x04];
        await using var destination = new MemoryStream();
        await destination.WriteAsync(original);
        var originalPosition = destination.Position;
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvReportExporter.WriteAsync(
                Snapshot(new AnalyticsDetailRow(
                    Guid.NewGuid(), Guid.NewGuid(), AnalyticsItemType.Todo, "取消",
                    null, null, ReminderImportance.Normal, DateTimeOffset.UnixEpoch,
                    null, null, null, null, AnalyticsRecordStatus.Incomplete)),
                ReportPrivacyMode.Full,
                destination,
                source.Token));

        Assert.Equal(original, destination.ToArray());
        Assert.Equal(originalPosition, destination.Position);
    }

    [Fact]
    public async Task Invalid_privacy_mode_is_rejected_before_writing_destination()
    {
        byte[] original = [0xFE, 0xED];
        await using var destination = new MemoryStream(original, writable: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CsvReportExporter.WriteAsync(
                Snapshot(), (ReportPrivacyMode)99, destination, CancellationToken.None));

        Assert.Equal(original, destination.ToArray());
        Assert.Equal(0, destination.Position);
    }

    private static AnalyticsSnapshot Snapshot(params AnalyticsDetailRow[] details) =>
        new(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Parse("2026-08-09T12:00:00.0000000+08:00"),
            new LocalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 9)),
            "UTC+08",
            new AnalyticsTotals(0, 0, 0, 0, 0, 0, 0, 0),
            [], [], [], [], details);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static byte[] Utf8BomBytes(string text) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
}
