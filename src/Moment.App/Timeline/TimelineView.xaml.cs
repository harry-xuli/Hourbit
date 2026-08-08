using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ItemsControl = System.Windows.Controls.ItemsControl;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
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
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (_viewModel is null)
            return;

        var modifiers = eventArgs.KeyboardDevice.Modifiers;
        if ((eventArgs.Key, modifiers) == (Key.N, ModifierKeys.Control))
        {
            ExecuteIfAvailable(_viewModel.OpenQuickAddCommand, eventArgs);
            return;
        }

        var command = (eventArgs.Key, modifiers) switch
        {
            (Key.Enter, ModifierKeys.None) => _viewModel.EditCommand,
            (Key.Delete, ModifierKeys.None) => _viewModel.DeleteCommand,
            (Key.Space, ModifierKeys.Control | ModifierKeys.Shift) =>
                _viewModel.CompleteCommand,
            _ => null
        };
        if (command is null)
            return;

        var row = FindTimelineRow(eventArgs.OriginalSource as DependencyObject)
            ?? FindTimelineRow(Keyboard.FocusedElement as DependencyObject);
        switch (row?.DataContext)
        {
            case TodoTimelineItemViewModel todo:
                _viewModel.SelectedTodo = todo;
                break;
            case TimelineItemViewModel reminder:
                _viewModel.SelectedItem = reminder;
                break;
            default:
                return;
        }

        ExecuteIfAvailable(command, eventArgs);
    }

    private static void ExecuteIfAvailable(
        System.Windows.Input.ICommand command,
        KeyEventArgs eventArgs)
    {
        if (!command.CanExecute(null))
            return;
        eventArgs.Handled = true;
        command.Execute(null);
    }

    private ListBoxItem? FindTimelineRow(DependencyObject? source)
    {
        if (source is null)
            return null;

        foreach (var list in DescendantLists(this))
        {
            if (ItemsControl.ContainerFromElement(list, source)
                is ListBoxItem row)
                return row;
        }
        return null;
    }

    private void OnTimelineSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (_isSynchronizingSelection)
            return;

        if (eventArgs.AddedItems.Count > 0
            && _viewModel is { } viewModel)
        {
            switch (eventArgs.AddedItems[0])
            {
                case TimelineItemViewModel reminder:
                    viewModel.SelectedItem = reminder;
                    break;
                case TodoTimelineItemViewModel todo:
                    viewModel.SelectedTodo = todo;
                    break;
                default:
                    return;
            }
            SynchronizeSelection();
        }
        else if (eventArgs.RemovedItems.Count > 0
            && _viewModel is { } currentViewModel)
        {
            if (currentViewModel.SelectedItem is { } selectedItem
                && eventArgs.RemovedItems.Contains(selectedItem))
            {
                currentViewModel.SelectedItem = null;
            }
            if (currentViewModel.SelectedTodo is { } selectedTodo
                && eventArgs.RemovedItems.Contains(selectedTodo))
            {
                currentViewModel.SelectedTodo = null;
            }
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
        if (eventArgs.PropertyName is nameof(TimelineViewModel.SelectedItem)
            or nameof(TimelineViewModel.SelectedTodo))
            SynchronizeSelection();
    }

    private void SynchronizeSelection()
    {
        if (_isSynchronizingSelection)
            return;

        _isSynchronizingSelection = true;
        try
        {
            object? selectedItem = (object?)_viewModel?.SelectedTodo
                ?? _viewModel?.SelectedItem;
            foreach (var list in DescendantLists(this))
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
