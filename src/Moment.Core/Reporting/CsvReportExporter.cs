using System.Globalization;
using System.Text;
using Moment.Core.Analytics;

namespace Moment.Core.Reporting;

public static class CsvReportExporter
{
    private static readonly string[] FullHeaders =
    [
        "RecordId",
        "ItemType",
        "Title",
        "Importance",
        "CreatedAt",
        "DueDate",
        "DueAt",
        "CompletedAt",
        "DeletedAt",
        "Status"
    ];

    private static readonly string[] AnonymousHeaders =
    [
        "ItemType",
        "Importance",
        "CreatedAt",
        "DueDate",
        "DueAt",
        "CompletedAt",
        "DeletedAt",
        "Status"
    ];

    public static async Task WriteAsync(
        AnalyticsSnapshot snapshot,
        ReportPrivacyMode privacy,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        if (privacy is not ReportPrivacyMode.Full and not ReportPrivacyMode.Anonymous)
        {
            throw new ArgumentOutOfRangeException(nameof(privacy));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        ct.ThrowIfCancellationRequested();

        var csv = new StringBuilder();
        AppendRow(csv, privacy == ReportPrivacyMode.Full ? FullHeaders : AnonymousHeaders);
        foreach (var row in snapshot.Details)
        {
            ct.ThrowIfCancellationRequested();
            AppendRow(csv, privacy == ReportPrivacyMode.Full
                ? FullFields(row)
                : AnonymousFields(row));
        }

        ct.ThrowIfCancellationRequested();
        var content = Encoding.UTF8.GetBytes(csv.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var payload = new byte[preamble.Length + content.Length];
        preamble.CopyTo(payload, 0);
        content.CopyTo(payload, preamble.Length);

        await destination.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
    }

    private static string[] FullFields(AnalyticsDetailRow row) =>
    [
        row.RecordId.ToString("D"),
        row.ItemType.ToString(),
        row.Title,
        row.Importance.ToString(),
        Format(row.CreatedAt),
        Format(row.DueDate),
        Format(row.DueAt),
        Format(row.CompletedAt),
        Format(row.DeletedAt),
        row.Status.ToString()
    ];

    private static string[] AnonymousFields(AnalyticsDetailRow row) =>
    [
        row.ItemType.ToString(),
        row.Importance.ToString(),
        Format(row.CreatedAt),
        Format(row.DueDate),
        Format(row.DueAt),
        Format(row.CompletedAt),
        Format(row.DeletedAt),
        row.Status.ToString()
    ];

    private static string Format(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string Format(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void AppendRow(StringBuilder csv, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                csv.Append(',');
            }

            AppendField(csv, fields[index]);
        }

        csv.Append("\r\n");
    }

    private static void AppendField(StringBuilder csv, string value)
    {
        if (!value.Contains(',') &&
            !value.Contains('"') &&
            !value.Contains('\r') &&
            !value.Contains('\n'))
        {
            csv.Append(value);
            return;
        }

        csv.Append('"');
        csv.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        csv.Append('"');
    }
}
