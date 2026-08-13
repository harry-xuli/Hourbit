using System.Collections.Immutable;

namespace Moment.App.Localization;

public static class LocalizationCatalog
{
    private static readonly ImmutableDictionary<string, string> Chinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action.New"] = "新建",
            ["Action.Help"] = "帮助",
            ["Action.Report"] = "报告",
            ["Action.Refresh"] = "刷新",
            ["Period.Day"] = "日",
            ["Period.Week"] = "周",
            ["Period.Month"] = "月",
            ["Section.Reminders"] = "定时提醒",
            ["Section.Todos"] = "待办事项",
            ["Search.Placeholder"] = "搜索提醒和待办",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action.New"] = "New",
            ["Action.Help"] = "Help",
            ["Action.Report"] = "Reports",
            ["Action.Refresh"] = "Refresh",
            ["Period.Day"] = "Day",
            ["Period.Week"] = "Week",
            ["Period.Month"] = "Month",
            ["Section.Reminders"] = "Reminders",
            ["Section.Todos"] = "To-do",
            ["Search.Placeholder"] = "Search reminders and to-dos",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static IReadOnlyList<string> Keys(UiLanguage language) =>
        Select(language).Keys.Order(StringComparer.Ordinal).ToArray();

    public static string Translate(UiLanguage language, string key) =>
        Select(language).TryGetValue(key, out var value) ? value : key;

    private static ImmutableDictionary<string, string> Select(UiLanguage language) =>
        language == UiLanguage.EnUs ? English : Chinese;
}
