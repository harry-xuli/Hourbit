using System.Globalization;
using System.Text;
using Hourbit.Core.Analytics;

namespace Hourbit.Core.Reporting;

public static class PdfReportExporter
{
    private const string FontName = "STSong-Light";

    public static async Task WriteAsync(
        AnalyticsSnapshot snapshot,
        ReportPrivacyMode privacy,
        string productName,
        string version,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(productName);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(destination);
        if (privacy is not ReportPrivacyMode.Full and not ReportPrivacyMode.Anonymous)
            throw new ArgumentOutOfRangeException(nameof(privacy));

        var lines = BuildLines(snapshot, privacy, productName, version);
        var content = BuildContentStream(lines);
        var pdf = BuildPdf(content);
        var bytes = Encoding.ASCII.GetBytes(pdf);
        await destination.WriteAsync(bytes, ct);
    }

    private static IReadOnlyList<string> BuildLines(
        AnalyticsSnapshot snapshot,
        ReportPrivacyMode privacy,
        string productName,
        string version)
    {
        var lines = new List<string>
        {
            $"{productName} {version} 分析报告",
            $"生成时间：{snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"日期范围：{snapshot.Range.Start:yyyy-MM-dd} 至 {snapshot.Range.End:yyyy-MM-dd}",
            $"时区：{snapshot.TimeZoneId}",
            string.Empty,
            $"已完成：{snapshot.Totals.Completed}",
            $"未来计划：{snapshot.Totals.FuturePlanned}",
            $"已逾期：{snapshot.Totals.Overdue}",
            $"活跃：{snapshot.Totals.Active}",
            $"删除：{snapshot.Totals.Deleted}",
            $"待办：{snapshot.Totals.Todos}",
            $"提醒：{snapshot.Totals.Reminders}",
            $"无日期待办：{snapshot.Totals.UndatedTodos}",
            string.Empty,
            "状态分布：",
        };
        lines.AddRange(snapshot.Status.Select(static slice => $"{slice.Label}：{slice.Count}"));
        lines.Add(string.Empty);
        lines.Add("类型分布：");
        lines.AddRange(snapshot.ItemTypes.Select(static slice => $"{slice.Label}：{slice.Count}"));
        lines.Add(string.Empty);
        lines.Add("重要性分布：");
        lines.AddRange(snapshot.Importance.Select(static slice => $"{slice.Label}：{slice.Count}"));
        lines.Add(string.Empty);
        lines.Add("完成趋势：");
        lines.AddRange(snapshot.Trend.Select(static bucket => $"{bucket.Label}：{bucket.Completed}"));

        lines.Add(string.Empty);
        lines.Add(privacy == ReportPrivacyMode.Full
            ? "本报告包含标题与记录标识。"
            : "本报告为匿名统计，不含标题与记录标识。");

        return lines;
    }

    private static string BuildContentStream(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("BT\n/F1 11 Tf\n");
        const double top = 790;
        const double leading = 16;
        for (var index = 0; index < lines.Count; index++)
        {
            var y = top - (index * leading);
            sb.Append("1 0 0 1 50 ")
              .Append(y.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" Tm <")
              .Append(EncodeUtf16Be(lines[index]))
              .Append("> Tj\n");
        }
        sb.Append("ET");
        return sb.ToString();
    }

    private static string EncodeUtf16Be(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (var character in text)
        {
            if (char.IsSurrogate(character))
            {
                sb.Append("FFFD");
                continue;
            }
            sb.Append(((ushort)character).ToString("X4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string BuildPdf(string contentStream)
    {
        var objects = new (int Number, string Body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            (4, $"<< /Type /Font /Subtype /Type0 /BaseFont /{FontName} /Encoding /UniGB-UCS2-H /DescendantFonts [6 0 R] >>"),
            (5, $"<< /Length {contentStream.Length} >>\nstream\n{contentStream}\nendstream"),
            (6, $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /{FontName} /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> /DW 1000 >>")
        };

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index] = builder.Length;
            builder.Append(objects[index].Number)
                   .Append(" 0 obj\n")
                   .Append(objects[index].Body)
                   .Append("\nendobj\n");
        }

        var xrefOffset = builder.Length;
        builder.Append("xref\n0 ")
               .Append(objects.Length + 1)
               .Append('\n')
               .Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture))
                   .Append(" 00000 n \n");
        }
        builder.Append("trailer\n<< /Size ")
               .Append(objects.Length + 1)
               .Append(" /Root 1 0 R >>\n")
               .Append("startxref\n")
               .Append(xrefOffset)
               .Append('\n')
               .Append("%%EOF\n");

        return builder.ToString();
    }
}
