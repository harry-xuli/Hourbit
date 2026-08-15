using System.IO;
using System.Text.RegularExpressions;

namespace Hourbit.App.Tests.Localization;

public sealed class HardcodedStringCoverageTests
{
    private static readonly string[] AuditedSurfaces =
    [
        "src/Hourbit.App/Timeline/TimelineView.xaml",
        "src/Hourbit.App/Timeline/DatePickerWindow.xaml",
        "src/Hourbit.App/Analytics/AnalyticsWindow.xaml",
        "src/Hourbit.App/Settings/SettingsView.xaml",
        "src/Hourbit.App/Help/HelpWindow.xaml",
        "src/Hourbit.App/Alerts/ImportantAlertWindow.xaml",
        "src/Hourbit.App/QuickAdd/QuickAddWindow.xaml"
    ];

    [Fact]
    public void Audited_UI_surfaces_do_not_hardcode_visible_Chinese_labels()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var literal = new Regex(
            @"(?:Text|Content|Title|AutomationProperties\.Name)=""([^""{}]*[\p{IsCJKUnifiedIdeographs}][^""{}]*)""",
            RegexOptions.Compiled);

        foreach (var relative in AuditedSurfaces)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                offenders.Add($"{relative}: file not found");
                continue;
            }

            var content = File.ReadAllText(path);
            foreach (Match match in literal.Matches(content))
            {
                var value = match.Groups[1].Value;
                if (value == "中")
                    continue;
                offenders.Add($"{relative}: \"{value}\"");
            }
        }

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
