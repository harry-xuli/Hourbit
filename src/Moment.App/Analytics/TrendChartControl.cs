using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Moment.Core.Analytics;
using Moment.App.Styles;
using System.Collections.Immutable;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;
using WpfPen = System.Windows.Media.Pen;
using WpfSystemColors = System.Windows.SystemColors;

namespace Moment.App.Analytics;

public sealed class TrendChartControl : FrameworkElement
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry),
        typeof(TrendGeometry),
        typeof(TrendChartControl),
        new FrameworkPropertyMetadata(
            TrendGeometry.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary),
        typeof(string),
        typeof(TrendChartControl),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender,
            static (target, args) => AutomationProperties.SetName(
                (TrendChartControl)target, args.NewValue as string ?? string.Empty)));

    public TrendChartControl()
    {
        Focusable = true;
        System.Windows.Input.KeyboardNavigation.SetIsTabStop(this, true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal int PaletteRevision { get; private set; }
    internal WpfBrush? LastPrimaryBrush { get; private set; }
    internal ImmutableArray<Rect> LastBarBounds { get; private set; } = [];

    public TrendGeometry Geometry
    {
        get => (TrendGeometry)GetValue(GeometryProperty);
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
        LastBarBounds = [];
        LastPrimaryBrush = null;
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        const double left = 10;
        const double right = 10;
        const double top = 10;
        const double labelHeight = 26;
        var baseline = Math.Max(top, ActualHeight - labelHeight);
        var chartWidth = Math.Max(0, ActualWidth - left - right);
        var chartHeight = Math.Max(0, baseline - top);
        var border = ResolveBrush("BorderBrush", WpfSystemColors.ActiveBorderBrush);
        drawingContext.DrawLine(new WpfPen(border, 1),
            new WpfPoint(left, baseline), new WpfPoint(ActualWidth - right, baseline));
        if (Geometry.Points.IsEmpty)
            return;

        var brush = ResolveBrush(Geometry.ColorKey, WpfSystemColors.HighlightBrush);
        LastPrimaryBrush = brush;
        var count = Geometry.Points.Length;
        var slotWidth = chartWidth / Math.Max(1, count);
        var barWidth = Math.Max(1, Math.Min(32, slotWidth * 0.7));
        var usableWidth = Math.Max(0, chartWidth - barWidth);
        var bounds = ImmutableArray.CreateBuilder<Rect>(count);
        foreach (var point in Geometry.Points)
        {
            var centerX = left + barWidth / 2 +
                (count == 1 ? usableWidth / 2 : point.X * usableWidth);
            var barTop = top + point.Y * chartHeight;
            var height = Math.Max(point.Value > 0 ? 1 : 0, baseline - barTop);
            if (height > 0)
            {
                var barBounds = new Rect(
                    centerX - barWidth / 2, baseline - height, barWidth, height);
                bounds.Add(barBounds);
                drawingContext.DrawRoundedRectangle(
                    brush, null, barBounds,
                    2, 2);
            }
            if (!point.ShowLabel)
                continue;
            var text = new FormattedText(
                point.Label,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                ResolveBrush("SecondaryTextBrush", WpfSystemColors.WindowTextBrush),
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(28, slotWidth * 1.8),
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center
            };
            drawingContext.DrawText(text,
                new WpfPoint(centerX - text.MaxTextWidth / 2, baseline + 4));
        }
        LastBarBounds = bounds.ToImmutable();
        if (IsKeyboardFocusWithin)
        {
            drawingContext.DrawRectangle(
                null,
                new WpfPen(ResolveBrush("FocusBrush", WpfSystemColors.HighlightBrush), 2),
                new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
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
        LastPrimaryBrush = ResolveBrush(
            Geometry.ColorKey, WpfSystemColors.HighlightBrush);
        InvalidateVisual();
    }
}
