using Moment.App.Input;
using Moment.App.Localization;

namespace Moment.App.Tests.Input;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void Footer_and_help_are_built_from_the_same_shortcut_definitions()
    {
        var footer = ShortcutCatalog.Footer(UiLanguage.ZhCn);
        var help = ShortcutCatalog.Help(UiLanguage.ZhCn);

        foreach (var gesture in new[]
                 { "Ctrl+N", "Ctrl+D", "Ctrl+Shift+Space", "Enter", "Delete", "Ctrl+F", "F5", "Esc" })
        {
            Assert.Contains(gesture, footer);
            Assert.Contains(gesture, help);
        }
    }

    [Fact]
    public void English_catalog_keeps_the_same_gestures()
    {
        Assert.Equal(
            ShortcutCatalog.Gestures(UiLanguage.ZhCn),
            ShortcutCatalog.Gestures(UiLanguage.EnUs));
    }
}
