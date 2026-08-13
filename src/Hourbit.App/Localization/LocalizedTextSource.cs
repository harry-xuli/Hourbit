using System.ComponentModel;

namespace Hourbit.App.Localization;

public sealed class LocalizedTextSource : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService _localization;

    public LocalizedTextSource(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _localization.Translate(key);

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs args) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

public static class LocalizationHub
{
    private static LocalizedTextSource _text = new(new LocalizationService(
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), null));

    public static LocalizedTextSource Text => _text;

    public static string Translate(string key) => _text[key];

    public static void Use(ILocalizationService localization)
    {
        var replacement = new LocalizedTextSource(localization);
        var previous = Interlocked.Exchange(ref _text, replacement);
        previous.Dispose();
    }
}
