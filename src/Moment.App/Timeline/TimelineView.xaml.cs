using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ListBox = System.Windows.Controls.ListBox;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Moment.App.Timeline;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _viewModel;
    private bool _isSynchronizingSelection;

    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnTimelineSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (_isSynchronizingSelection)
            return;

        if (eventArgs.AddedItems.Count > 0
            && eventArgs.AddedItems[0] is TimelineItemViewModel item
            && _viewModel is { } viewModel)
        {
            viewModel.SelectedItem = item;
            SynchronizeSelection();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachViewModel(DataContext as TimelineViewModel);
        SynchronizeSelection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) =>
        AttachViewModel(null);

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
            return;
        AttachViewModel(eventArgs.NewValue as TimelineViewModel);
        SynchronizeSelection();
    }

    private void AttachViewModel(TimelineViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(TimelineViewModel.SelectedItem))
            SynchronizeSelection();
    }

    private void SynchronizeSelection()
    {
        if (_isSynchronizingSelection)
            return;

        _isSynchronizingSelection = true;
        try
        {
            var selectedItem = _viewModel?.SelectedItem;
            foreach (var list in DescendantLists(GroupList))
            {
                var listSelection = selectedItem is not null && list.Items.Contains(selectedItem)
                    ? selectedItem
                    : null;
                if (!ReferenceEquals(list.SelectedItem, listSelection))
                    list.SelectedItem = listSelection;
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private static IEnumerable<ListBox> DescendantLists(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ListBox list)
                yield return list;
            foreach (var descendant in DescendantLists(child))
                yield return descendant;
        }
    }
}
