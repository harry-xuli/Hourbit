namespace Moment.App.Timeline;

public partial class DatePickerWindow : System.Windows.Window
{
    public DatePickerWindow(DateOnly current)
    {
        InitializeComponent();
        DateInput.SelectedDate = current.ToDateTime(TimeOnly.MinValue);
    }

    public DateOnly? SelectedDate => DateInput.SelectedDate is { } date
        ? DateOnly.FromDateTime(date)
        : null;

    private void OnAccept(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DateInput.SelectedDate is null)
            return;
        DialogResult = true;
    }
}

public sealed class WpfDatePicker(Func<System.Windows.Window?> owner) : IDatePicker
{
    public Task<DateOnly?> ChooseAsync(DateOnly current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var window = new DatePickerWindow(current) { Owner = owner() };
        var result = window.ShowDialog() == true ? window.SelectedDate : null;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
