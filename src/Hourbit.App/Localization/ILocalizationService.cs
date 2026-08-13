namespace Hourbit.App.Localization;

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    UiLanguage CurrentLanguage { get; }

    string PersistedCode { get; }

    string Translate(string key);

    void SetLanguage(UiLanguage language);
}
