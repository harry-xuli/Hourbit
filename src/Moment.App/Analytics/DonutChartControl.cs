using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Moment.Core.Analytics;
using Moment.App.Styles;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;
using WpfPen = System.Windows.Media.Pen;
using WpfSize = System.Windows.Size;
using WpfSystemColors = System.Windows.SystemColors;

namespace Moment.App.Analytics;

public sealed class DonutChartControl : FrameworkElement
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry),
        typeof(DonutGeometry),
        typeof(DonutChartControl),
        new FrameworkPropertyMetadata(
            DonutGeometry.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary),
        typeof(string),
        typeof(DonutChartControl),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender,
            static (target, args) => AutomationProperties.SetName(
                (DonutChartControl)target, args.NewValue as string ?? string.Empty)));

    public DonutChartControl()
    {
        Focusable = true;
        System.Windows.Input.KeyboardNavigation.SetIsTabStop(this, true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal int PaletteRevision { get; private set; }
    internal WpfBrush? LastPrimaryBrush { get; private set; }

    public DonutGeometry Geometry
    {
        get => (DonutGeometry)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        LastPrimaryBrush = null;
        var diameter = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - 12);
        if (diameter <= 0)
            return;
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var radius = diameter / 2;
        if (Geometry.IsEmpty)
        {
            drawingContext.DrawEllipse(
                null,
                new WpfPen(ResolveBrush("BorderBrush", WpfSystemColors.ActiveBorderBrush),
                    Math.Max(6, radius * 0.22)),
                center, radius * 0.78, radius * 0.78);
            return;
        }

        foreach (var sector in Geometry.Sectors)
        {
            var brush = ResolveBrush(sector.ColorKey, WpfSystemColors.HighlightBrush);
            LastPrimaryBrush ??= brush;
            if (sector.SweepAngle >= 359.999)
            {
                drawingContext.DrawEllipse(brush, null, center, radius, radius);
                continue;
            }
            drawingContext.DrawGeometry(
                brush, null, CreateSector(center, radius, sector.StartAngle, sector.SweepAngle));
        }
        drawingContext.DrawEllipse(
            ResolveBrush("WindowBackgroundBrush", WpfSystemColors.WindowBrush),
            null, center, radius * 0.55, radius * 0.55);
        if (IsKeyboardFocusWithin)
        {
            drawingContext.DrawEllipse(
                null,
                new WpfPen(ResolveBrush("FocusBrush", WpfSystemColors.HighlightBrush), 2),
                center, radius, radius);
        }
    }

    private WpfBrush ResolveBrush(string key, WpfBrush fallback) =>
        TryFindResource(key) as WpfBrush ?? fallback;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        HighContrastPalette.PaletteChanged -= OnPaletteChanged;
        HighContrastPalette.PaletteChanged += OnPaletteChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        HighContrastPalette.PaletteChanged -= OnPaletteChanged;

    private void OnPaletteChanged(object? sender, EventArgs args)
    {
        PaletteRevision++;
        LastPrimaryBrush = Geometry.Sectors.IsEmpty
            ? null
            : ResolveBrush(
                Geometry.Sectors[0].ColorKey, WpfSystemColors.HighlightBrush);
        InvalidateVisual();
    }

    private static Geometry CreateSector(
        WpfPoint center,
        double radius,
        double startAngle,
        double sweepAngle)
    {
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(center, isFilled: true, isClosed: true);
            context.LineTo(start, isStroked: false, isSmoothJoin: false);
            context.ArcTo(
                end,
                new WpfSize(radius, radius),
                0,
                sweepAngle > 180,
                SweepDirection.Clockwise,
                isStroked: false,
                isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180d;
        return new WpfPoint(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
