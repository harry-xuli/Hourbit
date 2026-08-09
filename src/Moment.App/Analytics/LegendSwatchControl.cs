using System.Windows;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using Moment.App.Styles;
using WpfBrush = System.Windows.Media.Brush;
using WpfPen = System.Windows.Media.Pen;
using WpfSize = System.Windows.Size;
using WpfSystemColors = System.Windows.SystemColors;

namespace Moment.App.Analytics;

public sealed class LegendSwatchControl : FrameworkElement
{
    public static readonly DependencyProperty ColorKeyProperty = DependencyProperty.Register(
        nameof(ColorKey), typeof(string), typeof(LegendSwatchControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public LegendSwatchControl()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string ColorKey
    {
        get => (string)GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize) => new(18, 18);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var fill = TryFindResource(ColorKey) as WpfBrush ?? WpfSystemColors.HighlightBrush;
        var border = TryFindResource("PrimaryTextBrush") as WpfBrush ??
            WpfSystemColors.WindowTextBrush;
        var rect = new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2));
        drawingContext.DrawRectangle(fill, new WpfPen(border, 1), rect);
        drawingContext.DrawLine(new WpfPen(border, 1), rect.BottomLeft, rect.TopRight);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        HighContrastPalette.PaletteChanged -= OnPaletteChanged;
        HighContrastPalette.PaletteChanged += OnPaletteChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        HighContrastPalette.PaletteChanged -= OnPaletteChanged;

    private void OnPaletteChanged(object? sender, EventArgs args) => InvalidateVisual();
}
