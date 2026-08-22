using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using ItemsControl = System.Windows.Controls.ItemsControl;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using ContextMenuEventArgs = System.Windows.Controls.ContextMenuEventArgs;
using Hourbit.Core.Search;
using UserControl = System.Windows.Controls.UserControl;

namespace Hourbit.App.Timeline;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _viewModel;
    private bool _isSynchronizingSelection;
    private readonly DispatcherTimer _countdownTimer;
    private Point? _todoDragStart;
    private const string TodoDragFormat = "Hourbit.TodoId";

    public TimelineView()
    {
        InitializeComponent();
        _countdownTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => _viewModel?.UpdateCountdowns(DateTimeOffset.Now),
            Dispatcher)
        {
            IsEnabled = false
        };
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

        if ((eventArgs.Key, modifiers) == (Key.F, ModifierKeys.Control))
        {
            eventArgs.Handled = true;
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            return;
        }

        if ((eventArgs.Key, modifiers) == (Key.Escape, ModifierKeys.None)
            && _viewModel.Search?.IsOpen == true)
        {
            ExecuteIfAvailable(_viewModel.Search.CloseCommand, eventArgs);
            return;
        }

        if ((eventArgs.Key, modifiers) == (Key.F5, ModifierKeys.None))
        {
            ExecuteIfAvailable(_viewModel.LoadCommand, eventArgs);
            return;
        }

        var command = (eventArgs.Key, modifiers) switch
        {
            (Key.D, ModifierKeys.Control) => _viewModel.CopyCommand,
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

    private void OnRowContextMenuOpening(
        object sender,
        ContextMenuEventArgs eventArgs)
    {
        if (sender is not DependencyObject source)
            return;
        var row = FindTimelineRow(source);
        if (row is null)
            return;
        row.IsSelected = true;
        row.Focus();
    }

    private void OnTodoPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs) =>
        _todoDragStart = eventArgs.GetPosition(PendingTodoList);

    private void OnTodoPreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (_todoDragStart is not { } start
            || eventArgs.LeftButton != MouseButtonState.Pressed)
            return;

        var current = eventArgs.GetPosition(PendingTodoList);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _todoDragStart = null;
        var row = FindTimelineRow(eventArgs.OriginalSource as DependencyObject);
        if (row?.DataContext is not TodoTimelineItemViewModel todo)
            return;
        row.IsSelected = true;
        var data = new DataObject(TodoDragFormat, todo.TodoId);
        DragDrop.DoDragDrop(PendingTodoList, data, DragDropEffects.Move);
    }

    private async void OnTodoDrop(
        object sender,
        DragEventArgs eventArgs)
    {
        _todoDragStart = null;
        if (_viewModel is null
            || !eventArgs.Data.GetDataPresent(TodoDragFormat)
            || eventArgs.Data.GetData(TodoDragFormat) is not Guid sourceId)
            return;

        var row = FindTimelineRow(eventArgs.OriginalSource as DependencyObject);
        if (row?.DataContext is not TodoTimelineItemViewModel target)
            return;

        eventArgs.Handled = true;
        await _viewModel.TryMoveTodoAsync(
            sourceId, target.TodoId, CancellationToken.None);
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachViewModel(DataContext as TimelineViewModel);
        _countdownTimer.Start();
        SynchronizeSelection();
    }

    private async void OnSearchResultDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs eventArgs)
    {
        if (_viewModel?.Search is not { } search
            || GlobalSearchResults.SelectedItem is not ItemSearchResult result)
            return;
        await search.SelectResultCommand.ExecuteAsync(result);
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        _countdownTimer.Stop();
        AttachViewModel(null);
    }

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
