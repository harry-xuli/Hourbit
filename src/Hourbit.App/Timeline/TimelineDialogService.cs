using Hourbit.Core.Domain;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public readonly record struct TodoDialogResult(bool RequiresCallerRefresh);

public interface ITodoDialogService
{
    Task<TodoDialogResult> EditTodoAsync(TodoItem item, CancellationToken ct);
    Task CopyTodoAsync(TodoTimelineItemViewModel item, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class TimelineDialogService : ITimelineDialogService, ITodoDialogService
{
    private readonly Action _openQuickAdd;
    private readonly TimeZoneInfo _zone;
    private readonly IClock _clock;
    private readonly IReminderService _reminders;
    private readonly ITodoService _todos;
    private readonly Func<CancellationToken, Task> _afterChanged;
    private (Guid OccurrenceId, SeriesScope Scope)? _selectedEditScope;

    public TimelineDialogService(
        Action openQuickAdd,
        TimeZoneInfo zone,
        IClock clock,
        IReminderService reminders,
        ITodoService todos,
        Func<CancellationToken, Task>? afterChanged = null)
    {
        _openQuickAdd = openQuickAdd ?? throw new ArgumentNullException(nameof(openQuickAdd));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _afterChanged = afterChanged ?? (_ => Task.CompletedTask);
    }

    public Task<SeriesScope?> SelectEditScopeAsync(
        TimelineItemViewModel item,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            "选择“是”只修改本次，选择“否”修改本次及以后。",
            "编辑重复提醒",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);
        SeriesScope? scope = result switch
        {
            System.Windows.MessageBoxResult.Yes => SeriesScope.OccurrenceOnly,
            System.Windows.MessageBoxResult.No => SeriesScope.ThisAndFuture,
            _ => null
        };
        _selectedEditScope = scope is null
            ? null
            : (item.OccurrenceId, scope.Value);
        return Task.FromResult(scope);
    }

    public Task<SeriesScope?> SelectDeleteScopeAsync(
        TimelineItemViewModel item,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            "选择“是”只删除本次，选择“否”删除本次及以后。",
            "删除重复提醒",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(result switch
        {
            System.Windows.MessageBoxResult.Yes => (SeriesScope?)SeriesScope.OccurrenceOnly,
            System.Windows.MessageBoxResult.No => SeriesScope.ThisAndFuture,
            _ => null
        });
    }

    public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            $"确定删除“{item.Title}”吗？",
            "删除提醒",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(result == System.Windows.MessageBoxResult.Yes);
    }

    public async Task<ReminderDraft?> EditAsync(
        TimelineItemViewModel item,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();

        var scope = SeriesScope.OccurrenceOnly;
        if (item.IsRecurring)
        {
            if (_selectedEditScope is not { } selected ||
                selected.OccurrenceId != item.OccurrenceId)
            {
                var explicitlySelected = await SelectEditScopeAsync(item, ct);
                if (explicitlySelected is null)
                    return null;
                scope = explicitlySelected.Value;
            }
            else
            {
                scope = selected.Scope;
            }
        }
        _selectedEditScope = null;

        var viewModel = new EditReminderViewModel(
            item,
            _zone,
            _reminders,
            _todos,
            scope,
            item.IsRecurring
                ? _ => Task.FromResult<SeriesScope?>(scope)
                : null,
            _afterChanged);
        var window = new EditReminderWindow { DataContext = viewModel };
        SetOwner(window);
        window.ShowDialog();

        // Persistence is intentionally owned by the view model so a failure can
        // remain visible in the open dialog. Returning null prevents the legacy
        // timeline caller from issuing a second edit operation.
        return null;
    }

    public Task<TodoDialogResult> EditTodoAsync(
        TodoItem item,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var viewModel = new EditTodoViewModel(
            item, _zone, _todos, _afterChanged);
        var window = new EditTodoWindow { DataContext = viewModel };
        SetOwner(window);
        window.ShowDialog();
        return Task.FromResult(new TodoDialogResult(
            RequiresCallerRefresh: false));
    }

    public void OpenQuickAdd() => _openQuickAdd();

    public Task CopyReminderAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var viewModel = EditReminderViewModel.CreateCopy(
            item, _zone, _clock, _reminders, _todos, _afterChanged);
        var window = new EditReminderWindow { DataContext = viewModel };
        SetOwner(window);
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task CopyTodoAsync(TodoTimelineItemViewModel item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var viewModel = EditTodoViewModel.CreateCopy(
            item.Item, _zone, _todos, _reminders, _afterChanged);
        var window = new EditTodoWindow { DataContext = viewModel };
        SetOwner(window);
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private static void SetOwner(System.Windows.Window window)
    {
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
    }
}
