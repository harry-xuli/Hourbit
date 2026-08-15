using System.Globalization;

namespace Hourbit.App.Localization;

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    UiLanguage CurrentLanguage { get; }

    string PersistedCode { get; }

    CultureInfo CurrentCulture { get; }

    string LanguageTag { get; }

    string Translate(string key);

    void SetLanguage(UiLanguage language);
}
