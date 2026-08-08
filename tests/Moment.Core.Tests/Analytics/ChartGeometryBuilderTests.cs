using Moment.Core.Analytics;

namespace Moment.Core.Tests.Analytics;

public sealed class ChartGeometryBuilderTests
{
    [Fact]
    public void Donut_sectors_cover_one_complete_circle_and_use_semantic_colors()
    {
        var geometry = ChartGeometryBuilder.CreateDonut(
        [
            new DistributionSlice("completed", "已完成", 2),
            new DistributionSlice("incomplete", "未完成", 3),
            new DistributionSlice("overdue", "已逾期", 1)
        ]);

        Assert.False(geometry.IsEmpty);
        Assert.Equal(6, geometry.Total);
        Assert.Equal(360d, geometry.Sectors.Sum(static sector => sector.SweepAngle), 10);
        Assert.Equal(
            ["ChartCompletedBrush", "ChartIncompleteBrush", "ChartOverdueBrush"],
            geometry.Sectors.Select(static sector => sector.ColorKey));
        Assert.Equal(0d, geometry.Sectors[0].StartAngle, 10);
        Assert.Equal(
            geometry.Sectors[0].SweepAngle,
            geometry.Sectors[1].StartAngle,
            10);
    }

    [Fact]
    public void All_zero_donut_values_produce_an_explicit_empty_geometry()
    {
        var geometry = ChartGeometryBuilder.CreateDonut(
        [
            new DistributionSlice("completed", "已完成", 0),
            new DistributionSlice("incomplete", "未完成", 0)
        ]);

        Assert.True(geometry.IsEmpty);
        Assert.Equal(0, geometry.Total);
        Assert.Empty(geometry.Sectors);
    }

    [Fact]
    public void Trend_coordinates_are_normalized_and_stable_for_the_same_values()
    {
        TrendBucket[] buckets =
        [
            new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), "08-01", 0),
            new(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 2), "08-02", 5),
            new(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), "08-03", 10)
        ];

        var first = ChartGeometryBuilder.CreateTrend(buckets);
        var second = ChartGeometryBuilder.CreateTrend(buckets);

        Assert.Equal(first.Points.ToArray(), second.Points.ToArray());
        Assert.False(first.IsEmpty);
        Assert.Equal("ChartCompletedBrush", first.ColorKey);
        Assert.Equal([0d, 0.5d, 1d], first.Points.Select(static point => point.X));
        Assert.Equal([1d, 0.5d, 0d], first.Points.Select(static point => point.Y));
        Assert.Equal([0, 5, 10], first.Points.Select(static point => point.Value));
    }

    [Fact]
    public void Zero_trend_retains_bucket_labels_but_marks_the_plot_empty()
    {
        var geometry = ChartGeometryBuilder.CreateTrend(
        [
            new TrendBucket(
                new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), "08-01", 0),
            new TrendBucket(
                new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 2), "08-02", 0)
        ]);

        Assert.True(geometry.IsEmpty);
        Assert.Equal(0, geometry.Maximum);
        Assert.Equal(["08-01", "08-02"],
            geometry.Points.Select(static point => point.Label));
        Assert.All(geometry.Points, static point => Assert.Equal(1d, point.Y));
    }

    [Fact]
    public void Dense_trends_limit_axis_labels_while_preserving_first_and_last()
    {
        var start = new DateOnly(2026, 1, 1);
        var buckets = Enumerable.Range(0, 365)
            .Select(index => new TrendBucket(
                start.AddDays(index), start.AddDays(index), index.ToString(), index % 7))
            .ToArray();

        var geometry = ChartGeometryBuilder.CreateTrend(buckets);
        var visible = geometry.Points.Where(static point => point.ShowLabel).ToArray();

        Assert.InRange(visible.Length, 2, 12);
        Assert.True(geometry.Points[0].ShowLabel);
        Assert.True(geometry.Points[^1].ShowLabel);
    }
}
