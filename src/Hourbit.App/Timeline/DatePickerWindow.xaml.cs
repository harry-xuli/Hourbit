using System.Windows.Markup;
using Hourbit.App.Commands;
using Hourbit.App.Localization;

namespace Hourbit.App.Timeline;

public partial class DatePickerWindow : System.Windows.Window
{
    private readonly ILocalizationService _localization;

    public DatePickerWindow(DateOnly current, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
        InitializeComponent();
        DataContext = new DatePickerDialogViewModel(localization);
        ApplyLanguage();
        DateInput.SelectedDate = current.ToDateTime(TimeOnly.MinValue);
        _localization.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
    }

    public DateOnly? SelectedDate => DateInput.SelectedDate is { } date
        ? DateOnly.FromDateTime(date)
        : null;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            ApplyLanguage();
        else
            Dispatcher.Invoke(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        var language = XmlLanguage.GetLanguage(_localization.LanguageTag);
        Language = language;
        DateInput.Language = language;
    }

    private void OnClosed(object? sender, EventArgs e) =>
        _localization.LanguageChanged -= OnLanguageChanged;

    private void OnAccept(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DateInput.SelectedDate is null)
            return;
        DialogResult = true;
    }
}

public sealed class DatePickerDialogViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;

    public DatePickerDialogViewModel(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string Title => _localization.Translate("DatePicker.Title");
    public string Heading => _localization.Translate("DatePicker.Heading");
    public string Description => _localization.Translate("DatePicker.Description");
    public string CancelText => _localization.Translate("DatePicker.Cancel");
    public string ViewText => _localization.Translate("DatePicker.View");
    public string AccessibleName => _localization.Translate("DatePicker.AccessibleName");

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(ViewText));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

public sealed class WpfDatePicker(
    Func<System.Windows.Window?> owner,
    ILocalizationService localization) : IDatePicker
{
    public Task<DateOnly?> ChooseAsync(DateOnly current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var window = new DatePickerWindow(current, localization) { Owner = owner() };
        var result = window.ShowDialog() == true ? window.SelectedDate : null;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
