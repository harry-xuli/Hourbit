using System.Collections.ObjectModel;
using Moment.App.Commands;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;

namespace Moment.App.Timeline;

public interface ITimelineDialogService
{
    Task<SeriesScope?> SelectEditScopeAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<SeriesScope?> SelectDeleteScopeAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct);
    void OpenQuickAdd();
}

public sealed class TimelineViewModel : ObservableObject
{
    private readonly ITimelineQuery _query;
    private readonly IClock _clock;
    private readonly IReminderService _reminders;
    private readonly IReminderActionService _actions;
    private readonly ITodoService _todos;
    private readonly ITimelineDialogService _dialogs;
    private readonly ITodoDialogService _todoDialogs;
    private readonly TimeZoneInfo _zone;
    private CancellationTokenSource? _loadCancellation;
    private TimelineItemViewModel? _selectedItem;
    private TodoTimelineItemViewModel? _selectedTodo;
    private string? _errorMessage;
    private int _todosCompletedToday;
    private int _remindersCompletedToday;

    public TimelineViewModel(
        ITimelineQuery query,
        IClock clock,
        IReminderService reminders,
        IReminderActionService actions,
        ITodoService todos,
        ITimelineDialogService dialogs,
        ITodoDialogService todoDialogs,
        TimeZoneInfo zone)
    {
        _query = query;
        _clock = clock;
        _reminders = reminders;
        _actions = actions;
        _todos = todos;
        _dialogs = dialogs;
        _todoDialogs = todoDialogs;
        _zone = zone;
        Groups = new[] { "已错过", "接下来", "已完成" }
            .Select(static name => new TimelineGroupViewModel(name)).ToArray();
        LoadCommand = new AsyncCommand((_, _) => LoadAsync());
        EditCommand = new AsyncCommand(
            (_, ct) => ObserveAsync(() => EditAsync(ct)), _ => HasSelection);
        DeleteCommand = new AsyncCommand(
            (_, ct) => ObserveAsync(() => DeleteAsync(ct)), _ => HasSelection);
        CompleteCommand = new AsyncCommand(
            (_, ct) => ObserveAsync(() => CompleteAsync(ct)),
            _ => SelectedItem is not null || SelectedTodo is { IsCompleted: false });
        OpenQuickAddCommand = new AsyncCommand((_, _) => ObserveAsync(() =>
        {
            _dialogs.OpenQuickAdd();
            return Task.CompletedTask;
        }));
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];
    public ObservableCollection<TodoTimelineItemViewModel> PendingTodos { get; } = [];
    public ObservableCollection<TodoTimelineItemViewModel> CompletedTodos { get; } = [];
    public IReadOnlyList<TimelineGroupViewModel> Groups { get; }
    public IAsyncCommand LoadCommand { get; }
    public IAsyncCommand EditCommand { get; }
    public IAsyncCommand DeleteCommand { get; }
    public IAsyncCommand CompleteCommand { get; }
    public IAsyncCommand OpenQuickAddCommand { get; }
    public string ProductName => "Hourbit 日程";
    public string DateText => TimeZoneInfo.ConvertTime(_clock.Now, _zone).ToString(
        "yyyy年M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    public string MonthText => TimeZoneInfo.ConvertTime(_clock.Now, _zone).ToString("M月",
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    public string DayText => TimeZoneInfo.ConvertTime(_clock.Now, _zone).Day.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
    public string WeekdayText => TimeZoneInfo.ConvertTime(_clock.Now, _zone).ToString("dddd",
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    public string NextReminderText => Items.FirstOrDefault(item => item.GroupName == "接下来") is { } next
        ? $"{next.TimeText} {next.Title}"
        : "无";
    public int CompletedCount => _todosCompletedToday + _remindersCompletedToday;
    public string CompletedTooltipText =>
        $"待办：{_todosCompletedToday}，提醒：{_remindersCompletedToday}";

    private bool HasSelection => SelectedItem is not null || SelectedTodo is not null;

    public TimelineItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;
            if (value is not null && _selectedTodo is not null)
            {
                _selectedTodo = null;
                OnPropertyChanged(nameof(SelectedTodo));
            }
            RaiseSelectionCommandsChanged();
        }
    }

    public TodoTimelineItemViewModel? SelectedTodo
    {
        get => _selectedTodo;
        set
        {
            if (!SetProperty(ref _selectedTodo, value))
                return;
            if (value is not null && _selectedItem is not null)
            {
                _selectedItem = null;
                OnPropertyChanged(nameof(SelectedItem));
            }
            RaiseSelectionCommandsChanged();
        }
    }

    private void RaiseSelectionCommandsChanged()
    {
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        CompleteCommand.RaiseCanExecuteChanged();
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public async Task LoadAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.Now, _zone).DateTime);
            var snapshot = await _query.GetTimelineAsync(
                localDate, _zone, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ErrorMessage = null;
            SelectedTodo = null;
            SelectedItem = null;
            PendingTodos.Clear();
            CompletedTodos.Clear();
            Items.Clear();
            foreach (var group in Groups)
                group.Items.Clear();
            var todoItems = snapshot.Todos
                .Select(row => new TodoTimelineItemViewModel(row, localDate))
                .ToArray();
            foreach (var todo in todoItems
                         .Where(static todo => !todo.IsCompleted)
                         .OrderBy(static todo => todo.DueOrder)
                         .ThenBy(static todo => todo.DueDate)
                         .ThenBy(static todo => todo.TodoId))
            {
                PendingTodos.Add(todo);
            }
            foreach (var todo in todoItems
                         .Where(static todo => todo.IsCompleted)
                         .OrderByDescending(static todo => todo.CompletedAt)
                         .ThenBy(static todo => todo.TodoId))
            {
                CompletedTodos.Add(todo);
            }
            foreach (var item in snapshot.Reminders
                         .Select(row => new TimelineItemViewModel(row, _clock.Now))
                         .OrderBy(item => item.GroupOrder)
                         .ThenBy(item => item.DueAt)
                         .ThenBy(item => item.OccurrenceId))
            {
                Items.Add(item);
                Groups.Single(group => group.Name == item.GroupName).Items.Add(item);
            }
            _todosCompletedToday = snapshot.TodosCompletedToday;
            _remindersCompletedToday = snapshot.RemindersCompletedToday;
            if (PendingTodos.FirstOrDefault() is { } firstTodo)
                SelectedTodo = firstTodo;
            else
                SelectedItem = Items.FirstOrDefault();
            OnPropertyChanged(nameof(NextReminderText));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(CompletedTooltipText));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCancellation, null, cancellation), cancellation))
                cancellation.Dispose();
        }
    }

    private async Task EditAsync(CancellationToken ct)
    {
        if (SelectedTodo is { } todo)
        {
            await _todoDialogs.EditTodoAsync(todo.Item, ct);
            await LoadAsync();
            return;
        }
        var item = SelectedItem;
        if (item is null)
            return;
        var scope = item.IsRecurring
            ? await _dialogs.SelectEditScopeAsync(item, ct)
            : SeriesScope.OccurrenceOnly;
        if (scope is null)
            return;
        var draft = await _dialogs.EditAsync(item, ct);
        if (draft is null)
            return;
        await _reminders.EditAsync(item.OccurrenceId, draft, scope.Value, ct);
        await LoadAsync();
    }

    private async Task DeleteAsync(CancellationToken ct)
    {
        if (SelectedTodo is { } todo)
        {
            await _todos.DeleteAsync(todo.TodoId, ct);
            await LoadAsync();
            return;
        }
        var item = SelectedItem;
        if (item is null)
            return;
        SeriesScope? scope;
        if (item.IsRecurring)
        {
            scope = await _dialogs.SelectDeleteScopeAsync(item, ct);
        }
        else
        {
            if (!await _dialogs.ConfirmDeleteAsync(item, ct))
                return;
            scope = SeriesScope.OccurrenceOnly;
        }
        if (scope is null)
            return;
        await _reminders.DeleteAsync(item.OccurrenceId, scope.Value, ct);
        await LoadAsync();
    }

    private async Task CompleteAsync(CancellationToken ct)
    {
        if (SelectedTodo is { IsCompleted: false } todo)
        {
            await _todos.CompleteAsync(todo.TodoId, ct);
            await LoadAsync();
            return;
        }
        var item = SelectedItem;
        if (item is null)
            return;
        await _actions.CompleteAsync(item.OccurrenceId, ct);
        await LoadAsync();
    }

    private async Task ObserveAsync(Func<Task> operation)
    {
        try
        {
            ErrorMessage = null;
            await operation();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }
}
