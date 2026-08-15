using System.IO;

namespace Hourbit.App.Tests.Timeline;

public sealed class TimelinePeriodSelectorXamlTests
{
    [Fact]
    public void Empty_period_keeps_both_C_style_panels_visible_with_localized_messages()
    {
        var timelineXaml = ReadRepositoryFile(
            "src", "Hourbit.App", "Timeline", "TimelineView.xaml");

        Assert.DoesNotContain("Visibility=\"{Binding IsTimelineEmpty", timelineXaml);
        Assert.Contains("Text=\"{Binding EmptyRemindersText}\"", timelineXaml);
        Assert.Contains("Text=\"{Binding EmptyTodosText}\"", timelineXaml);
    }

    [Fact]
    public void Period_selector_uses_equal_segmented_radio_buttons_and_category_colors()
    {
        var timelineXaml = ReadRepositoryFile(
            "src", "Hourbit.App", "Timeline", "TimelineView.xaml");
        var colorsXaml = ReadRepositoryFile(
            "src", "Hourbit.App", "Styles", "Colors.xaml");
        var highContrastSource = ReadRepositoryFile(
            "src", "Hourbit.App", "Styles", "HighContrastPalette.cs");

        Assert.Contains("x:Key=\"PeriodSegmentRadioButtonStyle\"", timelineXaml);
        Assert.Contains("Property=\"Width\" Value=\"104\"", timelineXaml);
        Assert.Contains("Property=\"Height\" Value=\"56\"", timelineXaml);
        Assert.Contains("Width=\"20\" Height=\"20\"", timelineXaml);
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
        Assert.Contains("#0B57D0", colorsXaml);
        Assert.Contains("#4F2ABF", colorsXaml);
        Assert.Contains("#FF5A00", colorsXaml);
        Assert.Contains("#FFF9F2", colorsXaml);

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
            "src", "Hourbit.App", "Timeline", "TimelineView.xaml");

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
        Assert.DoesNotContain("x:Name=\"CompletedTodosExpander\"", timelineXaml);
        Assert.DoesNotContain("x:Name=\"CompletedTodoList\"", timelineXaml);
    }

    [Fact]
    public void Warm_focus_shell_matches_the_approved_C_layout_contract()
    {
        var timelineXaml = ReadRepositoryFile(
            "src", "Hourbit.App", "Timeline", "TimelineView.xaml");
        var colorsXaml = ReadRepositoryFile(
            "src", "Hourbit.App", "Styles", "Colors.xaml");

        foreach (var requiredName in new[]
                 {
                     "TopCommandBar", "GlobalSearchBox", "SearchWatermark",
                     "ReportsButton", "NewReminderButton", "LanguageSelector",
                     "HelpButton", "PeriodNavigationBar", "TodayPeriodButton",
                     "ChooseDateButton", "MetricsStrip", "ReminderPanel", "TodoPanel"
                 })
        {
            Assert.Contains($"x:Name=\"{requiredName}\"", timelineXaml);
        }

        Assert.Contains("x:Key=\"MetricCardButtonStyle\"", timelineXaml);
        Assert.Contains("x:Key=\"PanelSurfaceStyle\"", timelineXaml);
        Assert.DoesNotContain("<ColumnDefinition Width=\"132\" />", timelineXaml);
        Assert.DoesNotContain("x:Name=\"CompletedSummary\"", timelineXaml);
        Assert.DoesNotContain("Text=\"今日完成\"", timelineXaml);
        Assert.DoesNotContain("Text=\"下一个提醒\"", timelineXaml);
        Assert.Contains("#0B57D0", colorsXaml);
        Assert.Contains("#FF5A00", colorsXaml);
        Assert.Contains("#FFF9F2", colorsXaml);
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
