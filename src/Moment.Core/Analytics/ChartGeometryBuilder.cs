using System.Collections.Immutable;

namespace Moment.Core.Analytics;

public static class ChartGeometryBuilder
{
    private const int MaximumVisibleTrendLabels = 12;

    public static DonutGeometry CreateDonut(
        IEnumerable<DistributionSlice> source)
    {
        var slices = (source ?? throw new ArgumentNullException(nameof(source)))
            .ToImmutableArray();
        if (slices.Any(static slice => slice.Count < 0))
            throw new ArgumentOutOfRangeException(nameof(source), "Chart values cannot be negative.");

        var total = slices.Sum(static slice => slice.Count);
        if (total == 0)
            return DonutGeometry.Empty;

        var sectors = ImmutableArray.CreateBuilder<DonutSector>();
        var positive = slices.Where(static slice => slice.Count > 0).ToArray();
        var startAngle = 0d;
        for (var index = 0; index < positive.Length; index++)
        {
            var slice = positive[index];
            var sweepAngle = index == positive.Length - 1
                ? 360d - startAngle
                : 360d * slice.Count / total;
            sectors.Add(new DonutSector(
                slice.Key,
                slice.Label,
                slice.Count,
                startAngle,
                sweepAngle,
                ResolveColorKey(slice.Key)));
            startAngle += sweepAngle;
        }

        return new DonutGeometry(total, false, sectors);
    }

    public static TrendGeometry CreateTrend(IEnumerable<TrendBucket> source)
    {
        var buckets = (source ?? throw new ArgumentNullException(nameof(source)))
            .ToImmutableArray();
        if (buckets.Any(static bucket => bucket.Completed < 0))
            throw new ArgumentOutOfRangeException(nameof(source), "Chart values cannot be negative.");
        if (buckets.IsEmpty)
            return TrendGeometry.Empty;

        var maximum = buckets.Max(static bucket => bucket.Completed);
        var lastIndex = buckets.Length - 1;
        var labelStride = buckets.Length <= MaximumVisibleTrendLabels
            ? 1
            : (int)Math.Ceiling(lastIndex / (double)(MaximumVisibleTrendLabels - 1));
        var points = buckets.Select((bucket, index) => new TrendPoint(
                bucket.Label,
                bucket.Completed,
                lastIndex == 0 ? 0.5d : index / (double)lastIndex,
                maximum == 0 ? 1d : 1d - bucket.Completed / (double)maximum,
                index == 0 || index == lastIndex || index % labelStride == 0))
            .ToImmutableArray();

        return new TrendGeometry(
            maximum,
            maximum == 0,
            "ChartCompletedBrush",
            points);
    }

    private static string ResolveColorKey(string key) => key switch
    {
        "completed" => "ChartCompletedBrush",
        "incomplete" => "ChartIncompleteBrush",
        "overdue" => "ChartOverdueBrush",
        "todo" => "ChartTodoBrush",
        "reminder" => "ChartReminderBrush",
        "normal" => "ChartNormalBrush",
        "important" => "ChartImportantBrush",
        _ => "ChartOtherBrush"
    };
}
