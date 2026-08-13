using System.Collections.Immutable;

namespace Hourbit.App.Localization;

public static class LocalizationCatalog
{
    private static readonly ImmutableDictionary<string, string> Chinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action.New"] = "新建提醒",
            ["Action.Help"] = "使用说明",
            ["Action.Report"] = "报告",
            ["Action.Refresh"] = "刷新",
            ["Action.Search"] = "搜索",
            ["Action.ChooseDate"] = "选择日期",
            ["Period.Day"] = "日",
            ["Period.Week"] = "周",
            ["Period.Month"] = "月",
            ["Section.Reminders"] = "定时提醒",
            ["Section.Todos"] = "待办事项",
            ["Search.Placeholder"] = "搜索提醒和待办",
            ["Help.Title"] = "Hourbit 使用说明",
            ["Help.QuickCreateHeading"] = "快速创建",
            ["Help.QuickCreateBody"] = "点击“新建提醒”或按 Ctrl+N。输入日期和时间会创建提醒；没有时间则创建无时间待办。支持 2026-10-03、10月3日、10月3号和 24 小时时间。",
            ["Help.TimeHeading"] = "时间表达",
            ["Help.TimeBody"] = "直接写“5点”时，若今天 5 点已过会安排到明天早上；写“下午5点”会识别为 17:00。",
            ["Help.RepeatHeading"] = "重复提醒",
            ["Help.RepeatBody"] = "可选择每天、工作日（周一至周五）或每周，并在每周模式中选择星期。",
            ["Help.HandleHeading"] = "处理提醒",
            ["Help.HandleBody"] = "通知出现后可选择完成、忽略或稍后提醒；处理成功后主时间线会自动刷新。",
            ["Help.ShortcutsHeading"] = "快捷键",
            ["Help.CountdownHeading"] = "倒计时",
            ["Help.CountdownBody"] = "可从托盘创建常用倒计时。主时间线会显示动态剩余时间，到期后显示“已到时”。",
            ["Help.DataHeading"] = "数据与升级",
            ["Help.DataBody"] = "安装版和免安装版都不会随安装包附带历史数据。以后升级 Hourbit 会保留本机已有客户数据；卸载前如需迁移，请先在设置中导出备份。",
            ["Help.ExitHeading"] = "退出程序",
            ["Help.ExitBody"] = "关闭主窗口只会隐藏 Hourbit。需要彻底退出时，请右键任务栏托盘图标并选择“退出”。",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action.New"] = "New",
            ["Action.Help"] = "Help",
            ["Action.Report"] = "Reports",
            ["Action.Refresh"] = "Refresh",
            ["Action.Search"] = "Search",
            ["Action.ChooseDate"] = "Choose date",
            ["Period.Day"] = "Day",
            ["Period.Week"] = "Week",
            ["Period.Month"] = "Month",
            ["Section.Reminders"] = "Reminders",
            ["Section.Todos"] = "To-do",
            ["Search.Placeholder"] = "Search reminders and to-dos",
            ["Help.Title"] = "Hourbit Help",
            ["Help.QuickCreateHeading"] = "Quick create",
            ["Help.QuickCreateBody"] = "Choose New or press Ctrl+N. A date and time creates a reminder; no time creates a to-do. Hourbit supports 2026-10-03, Chinese month/day forms, and 24-hour time.",
            ["Help.TimeHeading"] = "Time expressions",
            ["Help.TimeBody"] = "If you enter 5 o'clock after today's 05:00, Hourbit schedules tomorrow morning. Entering 5 PM resolves to 17:00.",
            ["Help.RepeatHeading"] = "Repeating reminders",
            ["Help.RepeatBody"] = "Choose daily, weekdays (Monday to Friday), or weekly and select the required weekdays.",
            ["Help.HandleHeading"] = "Handle reminders",
            ["Help.HandleBody"] = "Complete, ignore, or snooze a notification. The timeline refreshes after the action succeeds.",
            ["Help.ShortcutsHeading"] = "Shortcuts",
            ["Help.CountdownHeading"] = "Countdowns",
            ["Help.CountdownBody"] = "Create common countdowns from the tray. The timeline shows the remaining time and changes to due when time expires.",
            ["Help.DataHeading"] = "Data and upgrades",
            ["Help.DataBody"] = "Setup and portable packages contain no customer history. Upgrades preserve local Hourbit data. Export a backup from Settings before moving devices.",
            ["Help.ExitHeading"] = "Exit Hourbit",
            ["Help.ExitBody"] = "Closing the main window hides Hourbit. To exit fully, right-click the tray icon and choose Exit.",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static IReadOnlyList<string> Keys(UiLanguage language) =>
        Select(language).Keys.Order(StringComparer.Ordinal).ToArray();

    public static string Translate(UiLanguage language, string key) =>
        Select(language).TryGetValue(key, out var value) ? value : key;

    private static ImmutableDictionary<string, string> Select(UiLanguage language) =>
        language == UiLanguage.EnUs ? English : Chinese;
}
