using Hourbit.App.Localization;

namespace Hourbit.App.Input;

public sealed record ShortcutDefinition(string Gesture, string Chinese, string English);

public static class ShortcutCatalog
{
    private static readonly ShortcutDefinition[] Items =
    [
        new("Ctrl+N", "新建", "New"),
        new("Ctrl+D", "复制", "Copy"),
        new("Ctrl+Shift+Space", "完成", "Complete"),
        new("Enter", "编辑", "Edit"),
        new("Delete", "删除", "Delete"),
        new("Ctrl+F", "搜索", "Search"),
        new("F5", "刷新", "Refresh"),
        new("Esc", "关闭或隐藏", "Close or hide"),
    ];

    public static IReadOnlyList<string> Gestures(UiLanguage language) =>
        Items.Select(static item => item.Gesture).ToArray();

    public static string Footer(UiLanguage language) => Format(language, "   ");

    public static string Help(UiLanguage language) => Format(language, "；");

    private static string Format(UiLanguage language, string separator) =>
        string.Join(separator, Items.Select(item =>
            $"{item.Gesture} {(language == UiLanguage.EnUs ? item.English : item.Chinese)}"));
}
