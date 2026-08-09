using System.IO;

namespace Moment.App.Tests.Timeline;

public sealed class TimelinePeriodSelectorXamlTests
{
    [Fact]
    public void Period_selector_uses_equal_segmented_radio_buttons_and_category_colors()
    {
        var timelineXaml = ReadRepositoryFile(
            "src", "Moment.App", "Timeline", "TimelineView.xaml");
        var colorsXaml = ReadRepositoryFile(
            "src", "Moment.App", "Styles", "Colors.xaml");
        var highContrastSource = ReadRepositoryFile(
            "src", "Moment.App", "Styles", "HighContrastPalette.cs");

        Assert.Contains("x:Key=\"PeriodSegmentRadioButtonStyle\"", timelineXaml);
        Assert.Contains("Property=\"Width\" Value=\"78\"", timelineXaml);
        Assert.Contains("Property=\"Height\" Value=\"42\"", timelineXaml);
        Assert.Contains("Width=\"17\" Height=\"17\"", timelineXaml);
        Assert.Contains("VerticalAlignment=\"Center\"", timelineXaml);
        Assert.Contains("Property=\"IsChecked\" Value=\"True\"", timelineXaml);
        Assert.Contains("Property=\"IsKeyboardFocused\" Value=\"True\"", timelineXaml);
        Assert.Equal(
            3,
            CountOccurrences(timelineXaml, "<PathGeometry Figures=\""));
        Assert.DoesNotContain("Tag=\"M", timelineXaml);

        foreach (var name in new[]
                 {
                     "DayPeriodButton", "WeekPeriodButton", "MonthPeriodButton"
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", timelineXaml);
        }

        Assert.Equal(
            3,
            CountOccurrences(
                timelineXaml,
                "Style=\"{StaticResource PeriodSegmentRadioButtonStyle}\""));
        Assert.DoesNotContain("Content=\"年\"", timelineXaml);

        Assert.Contains("x:Key=\"PeriodDayBrush\"", colorsXaml);
        Assert.Contains("x:Key=\"PeriodWeekBrush\"", colorsXaml);
        Assert.Contains("x:Key=\"PeriodMonthBrush\"", colorsXaml);
        Assert.Contains("#0F5CC0", colorsXaml);
        Assert.Contains("#7C3AED", colorsXaml);
        Assert.Contains("#C45F00", colorsXaml);

        Assert.Contains("\"PeriodDayBrush\"", highContrastSource);
        Assert.Contains("\"PeriodWeekBrush\"", highContrastSource);
        Assert.Contains("\"PeriodMonthBrush\"", highContrastSource);
        Assert.Contains("\"PeriodDaySelectedBrush\"", highContrastSource);
        Assert.Contains("\"PeriodWeekSelectedBrush\"", highContrastSource);
        Assert.Contains("\"PeriodMonthSelectedBrush\"", highContrastSource);
    }

    [Fact]
    public void Timeline_places_reminders_left_and_todos_right_in_three_to_two_columns()
    {
        var timelineXaml = ReadRepositoryFile(
            "src", "Moment.App", "Timeline", "TimelineView.xaml");

        Assert.Contains("x:Name=\"TimelineColumns\"", timelineXaml);
        Assert.Contains("<ColumnDefinition Width=\"3*\"", timelineXaml);
        Assert.Contains("<ColumnDefinition Width=\"2*\"", timelineXaml);
        Assert.Contains(
            "x:Name=\"ReminderColumn\" Grid.Column=\"0\"",
            timelineXaml);
        Assert.Contains(
            "x:Name=\"TodoColumn\" Grid.Column=\"2\"",
            timelineXaml);
        Assert.Contains("x:Name=\"ReminderSectionHeader\"", timelineXaml);
        Assert.Contains("x:Name=\"TodoSectionHeader\"", timelineXaml);
        Assert.Contains("x:Name=\"CompletedTodosExpander\"", timelineXaml);
        Assert.Contains("IsExpanded=\"False\"", timelineXaml);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([repositoryRoot, .. segments]));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
