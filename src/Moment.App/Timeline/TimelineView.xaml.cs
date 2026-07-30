namespace Moment.App.Timeline;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    public TimelineView() => InitializeComponent();

    private void OnTimelineSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (eventArgs.AddedItems.Count > 0
            && eventArgs.AddedItems[0] is TimelineItemViewModel item
            && DataContext is TimelineViewModel viewModel)
        {
            viewModel.SelectedItem = item;
        }
    }
}
