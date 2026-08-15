using System.Globalization;

namespace Hourbit.App.Localization;

public sealed class LocalizationService : ILocalizationService
{
    public LocalizationService(CultureInfo windowsCulture, string? persistedCode)
    {
        CurrentLanguage = ParsePersisted(persistedCode)
            ?? (windowsCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.ZhCn
                : UiLanguage.EnUs);
        ApplyCulture();
    }

    public event EventHandler? LanguageChanged;

    public UiLanguage CurrentLanguage { get; private set; }

    public string PersistedCode => CurrentLanguage == UiLanguage.EnUs ? "en-US" : "zh-CN";

    public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo(
        CurrentLanguage == UiLanguage.EnUs ? "en-US" : "zh-CN");

    public string LanguageTag => CurrentCulture.Name;

    public string Translate(string key) => LocalizationCatalog.Translate(CurrentLanguage, key);

    public void SetLanguage(UiLanguage language)
    {
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        ApplyCulture();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    // WPF controls (DatePicker/Calendar) fall back to the thread's current
    // culture when their xml:lang is not set, so keep the thread culture in
    // sync with the app language instead of setting the Language property on
    // each control (which breaks the Calendar popup).
    private void ApplyCulture()
    {
        var culture = CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static UiLanguage? ParsePersisted(string? code) => code switch
    {
        "zh-CN" => UiLanguage.ZhCn,
        "en-US" => UiLanguage.EnUs,
        _ => null,
    };
}
