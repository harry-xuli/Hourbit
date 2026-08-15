using System.IO;
using Hourbit.Core.Analytics;
using Hourbit.Core.Reporting;

namespace Hourbit.App.Analytics;

public sealed class ReportExportService(string productName, string version)
{
    public async Task<IReadOnlyList<string>> ExportAsync(
        AnalyticsSnapshot snapshot,
        ReportPrivacyMode privacy,
        string basePath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var pdfPath = basePath + ".pdf";
        var csvPath = basePath + ".csv";
        var created = new List<string>();
        try
        {
            await WriteAtomicallyAsync(pdfPath, created, ct,
                (stream, token) => PdfReportExporter.WriteAsync(
                    snapshot, privacy, productName, version, stream, token));
            await WriteAtomicallyAsync(csvPath, created, ct,
                (stream, token) => CsvReportExporter.WriteAsync(
                    snapshot, privacy, stream, token));
            return [pdfPath, csvPath];
        }
        catch
        {
            foreach (var path in created)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            throw;
        }
    }

    private static async Task WriteAtomicallyAsync(
        string finalPath,
        List<string> created,
        CancellationToken ct,
        Func<Stream, CancellationToken, Task> write)
    {
        var temporary = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream, ct);
            }
            File.Move(temporary, finalPath, overwrite: true);
            created.Add(finalPath);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
