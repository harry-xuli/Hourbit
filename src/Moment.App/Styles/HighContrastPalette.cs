using System.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfSystemColors = System.Windows.SystemColors;

namespace Moment.App.Styles;

internal static class HighContrastPalette
{
    private static readonly string[] OverriddenKeys =
    [
        "WindowBackgroundBrush", "SubtleBackgroundBrush",
        "PrimaryTextBrush", "SecondaryTextBrush", "BorderBrush",
        "AccentBrush", "FocusBrush", "ImportantBrush", "MissedBrush",
        "CompletedBrush", "SelectionBackgroundBrush", "SelectionTextBrush",
        "ChartCompletedBrush", "ChartIncompleteBrush", "ChartOverdueBrush",
        "ChartTodoBrush", "ChartReminderBrush", "ChartNormalBrush",
        "ChartImportantBrush", "ChartOtherBrush"
    ];

    internal static void Apply(
        ResourceDictionary resources,
        bool enabled,
        Func<object, object?> findResource)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(findResource);

        foreach (var key in OverriddenKeys)
            resources.Remove(key);

        if (!enabled)
            return;

        WpfBrush Resolve(object key, WpfBrush fallback) =>
            findResource(key) as WpfBrush ?? fallback;

        var window = Resolve(
            WpfSystemColors.WindowBrushKey, WpfSystemColors.WindowBrush);
        var windowText = Resolve(
            WpfSystemColors.WindowTextBrushKey, WpfSystemColors.WindowTextBrush);
        var control = Resolve(
            WpfSystemColors.ControlBrushKey, WpfSystemColors.ControlBrush);
        var border = Resolve(
            WpfSystemColors.ActiveBorderBrushKey, WpfSystemColors.ActiveBorderBrush);
        var highlight = Resolve(
            WpfSystemColors.HighlightBrushKey, WpfSystemColors.HighlightBrush);
        var highlightText = Resolve(
            WpfSystemColors.HighlightTextBrushKey,
            WpfSystemColors.HighlightTextBrush);
        var grayText = Resolve(
            WpfSystemColors.GrayTextBrushKey, WpfSystemColors.GrayTextBrush);

        resources["WindowBackgroundBrush"] = window;
        resources["SubtleBackgroundBrush"] = control;
        resources["PrimaryTextBrush"] = windowText;
        resources["SecondaryTextBrush"] = windowText;
        resources["BorderBrush"] = border;
        resources["AccentBrush"] = highlight;
        resources["FocusBrush"] = highlight;
        resources["ImportantBrush"] = windowText;
        resources["MissedBrush"] = windowText;
        resources["CompletedBrush"] = windowText;
        resources["SelectionBackgroundBrush"] = highlight;
        resources["SelectionTextBrush"] = highlightText;
        resources["ChartCompletedBrush"] = highlight;
        resources["ChartIncompleteBrush"] = windowText;
        resources["ChartOverdueBrush"] = grayText;
        resources["ChartTodoBrush"] = highlight;
        resources["ChartReminderBrush"] = windowText;
        resources["ChartNormalBrush"] = grayText;
        resources["ChartImportantBrush"] = highlight;
        resources["ChartOtherBrush"] = windowText;
    }
}
