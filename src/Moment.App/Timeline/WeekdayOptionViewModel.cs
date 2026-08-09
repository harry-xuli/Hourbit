using Moment.App.Commands;

namespace Moment.App.Timeline;

public sealed class WeekdayOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public WeekdayOptionViewModel(DayOfWeek day, string label, bool isSelected)
    {
        Day = day;
        Label = label;
        _isSelected = isSelected;
    }

    public DayOfWeek Day { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
