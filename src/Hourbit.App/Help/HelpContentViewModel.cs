using Hourbit.App.Input;
using Hourbit.App.Localization;

namespace Hourbit.App.Help;

public sealed class HelpContentViewModel(ILocalizationService localization)
{
    private string T(string key) => localization.Translate(key);

    public string Title => T("Help.Title");
    public string QuickCreateHeading => T("Help.QuickCreateHeading");
    public string QuickCreateBody => T("Help.QuickCreateBody");
    public string TimeHeading => T("Help.TimeHeading");
    public string TimeBody => T("Help.TimeBody");
    public string RepeatHeading => T("Help.RepeatHeading");
    public string RepeatBody => T("Help.RepeatBody");
    public string HandleHeading => T("Help.HandleHeading");
    public string HandleBody => T("Help.HandleBody");
    public string ShortcutsHeading => T("Help.ShortcutsHeading");
    public string ShortcutsBody => ShortcutCatalog.Help(localization.CurrentLanguage);
    public string CountdownHeading => T("Help.CountdownHeading");
    public string CountdownBody => T("Help.CountdownBody");
    public string DataHeading => T("Help.DataHeading");
    public string DataBody => T("Help.DataBody");
    public string ExitHeading => T("Help.ExitHeading");
    public string ExitBody => T("Help.ExitBody");
}
