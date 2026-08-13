using System.Collections.ObjectModel;
using System.Globalization;
using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.App.Input;
using Hourbit.App.Search;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Analytics;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public interface ITimelineDialogService
{
    Task<SeriesScope?> SelectEditScopeAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<SeriesScope?> SelectDeleteScopeAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct);
    Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct);
    Task CopyReminderAsync(TimelineItemViewModel item, CancellationToken ct) =>
        Task.CompletedTask;
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
    private readonly CultureInfo _culture;
    private readonly Action<LocalDateRange> _openAnalytics;
    private readonly Action _openHelp;
    private readonly Action _openReports;
    private readonly ILocalizationService _localization;
    private readonly Func<UiLanguage, Task> _saveLanguage;
    private readonly IDatePicker _datePicker;
    private CancellationTokenSource? _loadCancellation;
    private TimelineItemViewModel? _selectedItem;
    private TodoTimelineItemViewModel? _selectedTodo;
    private string? _errorMessage;
    private int _todosCompletedToday;
    private int _remindersCompletedToday;
    private int _pastSevenDaysCompleted;
    private int _nextFourteenDaysPlanned;
    private LocalDateRange? _pastSevenDaysRange;
    private LocalDateRange? _nextFourteenDaysRange;
    private DateOnly _selectedDate;
    private TimelinePeriodKind _selectedPeriodKind;
    private TimelinePeriod _currentPeriod;

    public TimelineViewModel(
        ITimelineQuery query,
        IClock clock,
        IReminderService reminders,
        IReminderActionService actions,
        ITodoService todos,
        ITimelineDialogService dialogs,
        ITodoDialogService todoDialogs,
        TimeZoneInfo zone,
        CultureInfo? culture = null,
        Action<LocalDateRange>? openAnalytics = null,
        Action? openHelp = null,
        Action? openReports = null,
        ILocalizationService? localization = null,
        Func<UiLanguage, Task>? saveLanguage = null,
        IDatePicker? datePicker = null,
        SearchViewModel? search = null)
    {
        _query = query;
        _clock = clock;
        _reminders = reminders;
        _actions = actions;
        _todos = todos;
        _dialogs = dialogs;
        _todoDialogs = todoDialogs;
        _zone = zone;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _openAnalytics = openAnalytics ?? (_ => { });
        _openHelp = openHelp ?? (() => { });
        _openReports = openReports ?? (() => { });
        _localization = localization ?? new LocalizationService(_culture, null);
        _saveLanguage = saveLanguage ?? (_ => Task.CompletedTask);
        _datePicker = datePicker ?? new NullDatePicker();
        Search = search;
        _selectedDate = LocalToday;
        _selectedPeriodKind = TimelinePeriodKind.Day;
        _currentPeriod = TimelinePeriod.Create(
            _selectedDate, _selectedPeriodKind, _culture);
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
        CopyCommand = new AsyncCommand(
            (_, ct) => ObserveAsync(() => CopyAsync(ct)), _ => HasSelection);
        OpenQuickAddCommand = new AsyncCommand((_, _) => ObserveAsync(() =>
        {
            _dialogs.OpenQuickAdd();
            return Task.CompletedTask;
        }));
        OpenHelpCommand = new AsyncCommand((_, _) => ObserveAsync(() =>
        {
            _openHelp();
            return Task.CompletedTask;
        }));
        OpenReportsCommand = new AsyncCommand((_, _) => ObserveAsync(() =>
        {
            _openReports();
            return Task.CompletedTask;
        }));
        SelectChineseLanguageCommand = CreateLanguageSelectionCommand(UiLanguage.ZhCn);
        SelectEnglishLanguageCommand = CreateLanguageSelectionCommand(UiLanguage.EnUs);
        SelectDayPeriodCommand = CreatePeriodSelectionCommand(TimelinePeriodKind.Day);
        SelectWeekPeriodCommand = CreatePeriodSelectionCommand(TimelinePeriodKind.Week);
        SelectMonthPeriodCommand = CreatePeriodSelectionCommand(TimelinePeriodKind.Month);
        PreviousPeriodCommand = new AsyncCommand(
            (_, _) => ObserveAsync(() => MovePeriodAsync(-1)));
        NextPeriodCommand = new AsyncCommand(
            (_, _) => ObserveAsync(() => MovePeriodAsync(1)));
        ChooseDateCommand = new AsyncCommand(
            (_, ct) => ObserveAsync(() => ChooseDateAsync(ct)));
        OpenPastSevenDaysAnalyticsCommand = new AsyncCommand(
            (_, _) => ObserveAsync(() => OpenAnalyticsAsync(_pastSevenDaysRange)),
            _ => _pastSevenDaysRange is not null);
        OpenNextFourteenDaysAnalyticsCommand = new AsyncCommand(
            (_, _) => ObserveAsync(() => OpenAnalyticsAsync(_nextFourteenDaysRange)),
            _ => _nextFourteenDaysRange is not null);
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];
    public SearchViewModel? Search { get; }
    public ObservableCollection<TodoTimelineItemViewModel> PendingTodos { get; } = [];
    public ObservableCollection<TodoTimelineItemViewModel> CompletedTodos { get; } = [];
    public IReadOnlyList<TimelineGroupViewModel> Groups { get; }
    public IAsyncCommand LoadCommand { get; }
    public IAsyncCommand EditCommand { get; }
    public IAsyncCommand DeleteCommand { get; }
    public IAsyncCommand CompleteCommand { get; }
    public IAsyncCommand CopyCommand { get; }
    public IAsyncCommand OpenQuickAddCommand { get; }
    public IAsyncCommand OpenHelpCommand { get; }
    public IAsyncCommand OpenReportsCommand { get; }
    public IAsyncCommand SelectChineseLanguageCommand { get; }
    public IAsyncCommand SelectEnglishLanguageCommand { get; }
    public IAsyncCommand SelectDayPeriodCommand { get; }
    public IAsyncCommand SelectWeekPeriodCommand { get; }
    public IAsyncCommand SelectMonthPeriodCommand { get; }
    public IAsyncCommand PreviousPeriodCommand { get; }
    public IAsyncCommand NextPeriodCommand { get; }
    public IAsyncCommand ChooseDateCommand { get; }
    public IAsyncCommand OpenPastSevenDaysAnalyticsCommand { get; }
    public IAsyncCommand OpenNextFourteenDaysAnalyticsCommand { get; }
    public string ProductName => "Hourbit 日程";
    public UiLanguage CurrentLanguage => _localization.CurrentLanguage;
    public string NewText => _localization.Translate("Action.New");
    public string ReportsText => _localization.Translate("Action.Report");
    public string HelpText => _localization.Translate("Action.Help");
    public string SearchText => _localization.Translate("Action.Search");
    public string ChooseDateText => _localization.Translate("Action.ChooseDate");
    public string RemindersText => _localization.Translate("Section.Reminders");
    public string TodosText => _localization.Translate("Section.Todos");
    public string DayTextLabel => _localization.Translate("Period.Day");
    public string WeekTextLabel => _localization.Translate("Period.Week");
    public string MonthTextLabel => _localization.Translate("Period.Month");
    public string ShortcutFooter => ShortcutCatalog.Footer(CurrentLanguage);
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
    public TimelinePeriodKind SelectedPeriodKind => _selectedPeriodKind;
    public bool IsDayPeriodSelected =>
        _selectedPeriodKind == TimelinePeriodKind.Day;
    public bool IsWeekPeriodSelected =>
        _selectedPeriodKind == TimelinePeriodKind.Week;
    public bool IsMonthPeriodSelected =>
        _selectedPeriodKind == TimelinePeriodKind.Month;
    public TimelinePeriod CurrentPeriod => _currentPeriod;
    public string PeriodLabel => _currentPeriod.Label;
    public DateOnly SelectedDate => _selectedDate;
    public int PastSevenDaysCompleted => _pastSevenDaysCompleted;
    public int NextFourteenDaysPlanned => _nextFourteenDaysPlanned;

    private DateOnly LocalToday => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(_clock.Now, _zone).DateTime);

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
        CopyCommand.RaiseCanExecuteChanged();
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
            var localDate = LocalToday;
            var period = _currentPeriod;
            var now = _clock.Now;
            var snapshot = await _query.GetTimelineAsync(
                period.Range,
                now,
                _zone,
                cancellation.Token);
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
            _pastSevenDaysCompleted = snapshot.PastSevenDaysCompleted;
            _nextFourteenDaysPlanned = snapshot.NextFourteenDaysPlanned;
            _pastSevenDaysRange = snapshot.PastSevenDaysRange;
            _nextFourteenDaysRange = snapshot.NextFourteenDaysRange;
            if (PendingTodos.FirstOrDefault() is { } firstTodo)
                SelectedTodo = firstTodo;
            else
                SelectedItem = Items.FirstOrDefault();
            OnPropertyChanged(nameof(NextReminderText));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(CompletedTooltipText));
            OnPropertyChanged(nameof(PastSevenDaysCompleted));
            OnPropertyChanged(nameof(NextFourteenDaysPlanned));
            OpenPastSevenDaysAnalyticsCommand.RaiseCanExecuteChanged();
            OpenNextFourteenDaysAnalyticsCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested)
                ErrorMessage = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCancellation, null, cancellation), cancellation))
                cancellation.Dispose();
        }
    }

    private IAsyncCommand CreatePeriodSelectionCommand(TimelinePeriodKind kind) =>
        new AsyncCommand((_, _) => ObserveAsync(() => SelectPeriodAsync(kind)));

    private IAsyncCommand CreateLanguageSelectionCommand(UiLanguage language) =>
        new AsyncCommand((_, _) => ObserveAsync(() => SelectLanguageAsync(language)));

    private async Task SelectLanguageAsync(UiLanguage language)
    {
        if (_localization.CurrentLanguage == language)
            return;
        await _saveLanguage(language);
        _localization.SetLanguage(language);
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(NewText));
        OnPropertyChanged(nameof(ReportsText));
        OnPropertyChanged(nameof(HelpText));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(ChooseDateText));
        OnPropertyChanged(nameof(RemindersText));
        OnPropertyChanged(nameof(TodosText));
        OnPropertyChanged(nameof(DayTextLabel));
        OnPropertyChanged(nameof(WeekTextLabel));
        OnPropertyChanged(nameof(MonthTextLabel));
        OnPropertyChanged(nameof(ShortcutFooter));
    }

    private async Task SelectPeriodAsync(TimelinePeriodKind kind)
    {
        SetPeriod(kind, _selectedDate);
        await LoadAsync();
    }

    private async Task MovePeriodAsync(int direction)
    {
        _selectedDate = _selectedPeriodKind switch
        {
            TimelinePeriodKind.Day => _selectedDate.AddDays(direction),
            TimelinePeriodKind.Week => _selectedDate.AddDays(checked(direction * 7)),
            TimelinePeriodKind.Month => _selectedDate.AddMonths(direction),
            _ => throw new InvalidOperationException("Unknown timeline period kind.")
        };
        SetPeriod(_selectedPeriodKind, _selectedDate);
        await LoadAsync();
    }

    private void SetPeriod(TimelinePeriodKind kind, DateOnly selectedDate)
    {
        _selectedDate = selectedDate;
        _selectedPeriodKind = kind;
        _currentPeriod = TimelinePeriod.Create(selectedDate, kind, _culture);
        PublishPeriodChanged();
    }

    private void PublishPeriodChanged()
    {
        OnPropertyChanged(nameof(SelectedPeriodKind));
        OnPropertyChanged(nameof(IsDayPeriodSelected));
        OnPropertyChanged(nameof(IsWeekPeriodSelected));
        OnPropertyChanged(nameof(IsMonthPeriodSelected));
        OnPropertyChanged(nameof(CurrentPeriod));
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(SelectedDate));
    }

    private Task OpenAnalyticsAsync(LocalDateRange? range)
    {
        if (range is not null)
            _openAnalytics(range);
        return Task.CompletedTask;
    }

    private async Task EditAsync(CancellationToken ct)
    {
        if (SelectedTodo is { } todo)
        {
            var result = await _todoDialogs.EditTodoAsync(todo.Item, ct);
            if (result.RequiresCallerRefresh)
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

    private async Task ChooseDateAsync(CancellationToken ct)
    {
        var selected = await _datePicker.ChooseAsync(_selectedDate, ct);
        if (selected is null)
            return;
        SetPeriod(_selectedPeriodKind, selected.Value);
        await LoadAsync();
    }

    public async Task NavigateToDateAsync(DateOnly date)
    {
        SetPeriod(_selectedPeriodKind, date);
        await LoadAsync();
    }

    public void UpdateCountdowns(DateTimeOffset now)
    {
        foreach (var item in Items)
            item.UpdateNow(now);
    }

    private Task CopyAsync(CancellationToken ct)
    {
        if (SelectedTodo is { } todo)
            return _todoDialogs.CopyTodoAsync(todo, ct);
        return SelectedItem is { } reminder
            ? _dialogs.CopyReminderAsync(reminder, ct)
            : Task.CompletedTask;
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
