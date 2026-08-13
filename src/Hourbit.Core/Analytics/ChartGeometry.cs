using System.Collections.Immutable;

namespace Hourbit.Core.Analytics;

public sealed record DonutSector(
    string Key,
    string Label,
    int Value,
    double StartAngle,
    double SweepAngle,
    string ColorKey);

public sealed record DonutGeometry
{
    public DonutGeometry(
        int total,
        bool isEmpty,
        IEnumerable<DonutSector> sectors)
    {
        if (total < 0)
            throw new ArgumentOutOfRangeException(nameof(total));
        Total = total;
        IsEmpty = isEmpty;
        Sectors = (sectors ?? throw new ArgumentNullException(nameof(sectors)))
            .ToImmutableArray();
    }

    public int Total { get; }
    public bool IsEmpty { get; }
    public ImmutableArray<DonutSector> Sectors { get; }

    public static DonutGeometry Empty { get; } = new(0, true, []);
}

public sealed record TrendPoint(
    string Label,
    int Value,
    double X,
    double Y,
    bool ShowLabel);

public sealed record TrendGeometry
{
    public TrendGeometry(
        int maximum,
        bool isEmpty,
        string colorKey,
        IEnumerable<TrendPoint> points)
    {
        if (maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        Maximum = maximum;
        IsEmpty = isEmpty;
        ColorKey = colorKey ?? throw new ArgumentNullException(nameof(colorKey));
        Points = (points ?? throw new ArgumentNullException(nameof(points)))
            .ToImmutableArray();
    }

    public int Maximum { get; }
    public bool IsEmpty { get; }
    public string ColorKey { get; }
    public ImmutableArray<TrendPoint> Points { get; }

    public static TrendGeometry Empty { get; } =
        new(0, true, "ChartCompletedBrush", []);
}
