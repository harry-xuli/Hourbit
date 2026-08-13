using System.Globalization;
using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public enum EditRecurrenceMode
{
    None,
    Daily,
    Weekdays,
    Weekly
}

public sealed record EditOption<T>(T Value, string Label);

public sealed class EditReminderViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, DayOfWeek> DayLabels =
        new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
        {
            ["Weekday.Monday"] = DayOfWeek.Monday,
            ["Weekday.Tuesday"] = DayOfWeek.Tuesday,
            ["Weekday.Wednesday"] = DayOfWeek.Wednesday,
            ["Weekday.Thursday"] = DayOfWeek.Thursday,
            ["Weekday.Friday"] = DayOfWeek.Friday,
            ["Weekday.Saturday"] = DayOfWeek.Saturday,
            ["Weekday.Sunday"] = DayOfWeek.Sunday
        };

    private readonly TimeZoneInfo _zone;
    private IReminderService? _reminderService;
    private ITodoService? _todoService;
    private SeriesScope _editScope;
    private bool _sourceIsRecurring;
    private Func<CancellationToken, Task<SeriesScope?>>? _selectConversionScope;
    private Func<CancellationToken, Task> _afterSaved = _ => Task.CompletedTask;
    private string _title;
    private string _dateText;
    private string _timeText;
    private ReminderKind _selectedKind;
    private ReminderImportance _selectedImportance;
    private EditRecurrenceMode _selectedRecurrence;
    private string? _errorMessage;
    private ReminderDraft? _persistedEditDraft;
    private SeriesScope _persistedEditScope;
    private string? _refreshOnlyMessage;
    private bool _createMode;
    private int _operationInProgress;

    public EditReminderViewModel(TimelineItemViewModel item, TimeZoneInfo zone)
        : this(CreateDraft(item), zone)
    {
        ArgumentNullException.ThrowIfNull(item);
    }

    public EditReminderViewModel(ReminderDraft draft, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        var localDue = TimeZoneInfo.ConvertTime(draft.DueAt, zone);
        _title = draft.Title;
        _dateText = localDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _timeText = localDue.ToString("HH:mm", CultureInfo.InvariantCulture);
        _selectedKind = draft.Kind;
        _selectedImportance = draft.Importance;
        _selectedRecurrence = draft.Recurrence?.Kind switch
        {
            RecurrenceKind.Daily => EditRecurrenceMode.Daily,
            RecurrenceKind.Weekdays => EditRecurrenceMode.Weekdays,
            RecurrenceKind.Weekly => EditRecurrenceMode.Weekly,
            _ => EditRecurrenceMode.None
        };
        Weekdays = DayLabels
            .Select(pair => new WeekdayOptionViewModel(
                pair.Value,
                LocalizationHub.Translate(pair.Key),
                draft.Recurrence?.DaysOfWeek.Contains(pair.Value) == true))
            .ToArray();
        SaveCommand = new AsyncCommand((_, ct) => SaveAsync(ct), _ => !IsBusy);
    }

    public EditReminderViewModel(
        ReminderDraft draft,
        TimeZoneInfo zone,
        IReminderService reminderService,
        ITodoService todoService,
        Func<CancellationToken, Task>? afterSaved = null)
        : this(draft, zone)
    {
        _reminderService = reminderService ??
            throw new ArgumentNullException(nameof(reminderService));
        _todoService = todoService ??
            throw new ArgumentNullException(nameof(todoService));
        _afterSaved = afterSaved ?? (_ => Task.CompletedTask);
        _createMode = true;
    }

    public EditReminderViewModel(
        TimelineItemViewModel item,
        TimeZoneInfo zone,
        IReminderService reminderService,
        ITodoService todoService,
        SeriesScope editScope,
        Func<CancellationToken, Task<SeriesScope?>>? selectConversionScope = null,
        Func<CancellationToken, Task>? afterSaved = null)
        : this(item, zone)
    {
        _reminderService = reminderService ??
            throw new ArgumentNullException(nameof(reminderService));
        _todoService = todoService ??
            throw new ArgumentNullException(nameof(todoService));
        if (!Enum.IsDefined(editScope))
            throw new ArgumentOutOfRangeException(nameof(editScope));
        _editScope = editScope;
        _sourceIsRecurring = item.IsRecurring;
        _selectConversionScope = selectConversionScope;
        _afterSaved = afterSaved ?? (_ => Task.CompletedTask);
        OccurrenceId = item.OccurrenceId;
    }

    public event EventHandler? CloseRequested;

    public Guid OccurrenceId { get; }
    public string EditorTitle => LocalizationHub.Translate(
        _createMode ? "Editor.NewReminderCopy" : "Editor.EditReminder");
    public IAsyncCommand SaveCommand { get; }
    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;
    public bool IsRefreshOnly => _refreshOnlyMessage is not null;
    public bool CanEdit => !IsBusy && !IsRefreshOnly;
    public bool CanCancel => CanEdit;
    public string PrimaryActionText => LocalizationHub.Translate(
        IsRefreshOnly ? "Editor.RetryRefresh" : "Editor.Save");

    public IReadOnlyList<EditOption<ReminderKind>> Kinds { get; } =
    [
        new(ReminderKind.Countdown, LocalizationHub.Translate("Kind.Countdown")),
        new(ReminderKind.Alarm, LocalizationHub.Translate("Kind.Alarm")),
        new(ReminderKind.Plan, LocalizationHub.Translate("Kind.Plan"))
    ];

    public IReadOnlyList<EditOption<ReminderImportance>> Importances { get; } =
    [
        new(ReminderImportance.Normal, LocalizationHub.Translate("Importance.Normal")),
        new(ReminderImportance.Important, LocalizationHub.Translate("Importance.Important"))
    ];

    public IReadOnlyList<EditOption<EditRecurrenceMode>> Recurrences { get; } =
    [
        new(EditRecurrenceMode.None, LocalizationHub.Translate("Recurrence.None")),
        new(EditRecurrenceMode.Daily, LocalizationHub.Translate("Recurrence.Daily")),
        new(EditRecurrenceMode.Weekdays, LocalizationHub.Translate("Recurrence.Weekdays")),
        new(EditRecurrenceMode.Weekly, LocalizationHub.Translate("Recurrence.Weekly"))
    ];

    public string Title
    {
        get => _title;
        set
        {
            if (CanEdit)
                SetProperty(ref _title, value ?? string.Empty);
        }
    }

    public string DateText
    {
        get => _dateText;
        set
        {
            if (CanEdit)
                SetProperty(ref _dateText, value ?? string.Empty);
        }
    }

    public string TimeText
    {
        get => _timeText;
        set
        {
            if (!CanEdit)
                return;
            if (SetProperty(ref _timeText, value ?? string.Empty))
                OnPropertyChanged(nameof(ConvertsToTodo));
        }
    }

    public ReminderKind SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (CanEdit)
                SetProperty(ref _selectedKind, value);
        }
    }

    public ReminderImportance SelectedImportance
    {
        get => _selectedImportance;
        set
        {
            if (CanEdit)
                SetProperty(ref _selectedImportance, value);
        }
    }

    public EditRecurrenceMode SelectedRecurrence
    {
        get => _selectedRecurrence;
        set
        {
            if (!CanEdit)
                return;
            if (SetProperty(ref _selectedRecurrence, value))
                OnPropertyChanged(nameof(IsWeekly));
        }
    }

    public IReadOnlyList<WeekdayOptionViewModel> Weekdays { get; }

    public bool IsWeekly => SelectedRecurrence == EditRecurrenceMode.Weekly;
    public bool ConvertsToTodo => string.IsNullOrWhiteSpace(TimeText);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public static EditReminderViewModel CreateCopy(
        TimelineItemViewModel source,
        TimeZoneInfo zone,
        IClock clock,
        IReminderService reminderService,
        ITodoService todoService,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(clock);
        var localNow = TimeZoneInfo.ConvertTime(clock.Now, zone);
        var nextMinute = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day,
            localNow.Hour, localNow.Minute, 0, localNow.Offset).AddMinutes(1);
        var sourceDraft = CreateDraft(source);
        var recurrence = sourceDraft.Recurrence switch
        {
            null => null,
            { Kind: RecurrenceKind.Daily } => RecurrenceRule.Daily(
                TimeOnly.FromDateTime(nextMinute.DateTime)),
            { Kind: RecurrenceKind.Weekdays } => RecurrenceRule.Weekdays(
                TimeOnly.FromDateTime(nextMinute.DateTime)),
            { Kind: RecurrenceKind.Weekly } rule => RecurrenceRule.Weekly(
                rule.DaysOfWeek,
                TimeOnly.FromDateTime(nextMinute.DateTime)),
            _ => throw new InvalidOperationException("Unsupported recurrence rule.")
        };
        return new EditReminderViewModel(
            new ReminderDraft(
                source.Title,
                nextMinute,
                source.Kind,
                source.Importance,
                recurrence),
            zone,
            reminderService,
            todoService,
            afterSaved);
    }

    public bool TryBuildDraft(out ReminderDraft? draft)
    {
        draft = null;
        var title = Title.Trim();
        if (title.Length == 0)
        {
            ErrorMessage = "请输入提醒内容。";
            return false;
        }

        if (!DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || !TimeOnly.TryParseExact(TimeText, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            ErrorMessage = "请输入有效的日期和时间。";
            return false;
        }

        var localDue = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(localDue))
        {
            ErrorMessage = "该本地时间不存在，请选择其他时间。";
            return false;
        }

        RecurrenceRule? recurrence;
        switch (SelectedRecurrence)
        {
            case EditRecurrenceMode.None:
                recurrence = null;
                break;
            case EditRecurrenceMode.Daily:
                recurrence = RecurrenceRule.Daily(time);
                break;
            case EditRecurrenceMode.Weekdays:
                recurrence = RecurrenceRule.Weekdays(time);
                break;
            case EditRecurrenceMode.Weekly:
                var days = Weekdays
                    .Where(option => option.IsSelected)
                    .Select(option => option.Day)
                    .Distinct()
                    .ToArray();
                if (days.Length == 0)
                {
                    ErrorMessage = "请至少选择一个星期几。";
                    return false;
                }
                recurrence = RecurrenceRule.Weekly(days, time);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var dueAt = ResolveLocal(localDue);
        draft = new ReminderDraft(title, dueAt, SelectedKind, SelectedImportance, recurrence);
        ErrorMessage = null;
        return true;
    }

    public Task SaveAsync() => SaveAsync(CancellationToken.None);

    public async Task SaveAsync(CancellationToken ct)
    {
        if (_reminderService is null || _todoService is null)
        {
            throw new InvalidOperationException(
                "This reminder editor is not connected to persistence.");
        }

        if (!TryBeginOperation())
            return;
        var ordinaryEditAwaitingRefresh = false;
        try
        {
            if (IsRefreshOnly)
            {
                await FinishRefreshAsync(ct);
                return;
            }

            if (!ConvertsToTodo)
            {
                if (!TryBuildDraft(out var reminderDraft))
                    return;
                if (_createMode)
                {
                    await _reminderService.CreateAsync(reminderDraft!, ct);
                    EnterRefreshOnly(
                        "提醒副本已创建，但时间轴刷新失败。请仅重试刷新。");
                    await FinishRefreshAsync(ct);
                    return;
                }
                if (!ReminderDraftEquals(
                        _persistedEditDraft,
                        _persistedEditScope,
                        reminderDraft!,
                        _editScope))
                {
                    await _reminderService.EditAsync(
                        OccurrenceId, reminderDraft!, _editScope, ct);
                    _persistedEditDraft = reminderDraft;
                    _persistedEditScope = _editScope;
                }
                ordinaryEditAwaitingRefresh = true;
                await FinishRefreshAsync(ct);
                return;
            }

            if (!TryBuildTodoDraft(out var todoDraft))
                return;

            if (_createMode)
            {
                await _todoService.CreateAsync(todoDraft!, ct);
                EnterRefreshOnly(
                    "待办副本已创建，但时间轴刷新失败。请仅重试刷新。");
                await FinishRefreshAsync(ct);
                return;
            }

            SeriesScope conversionScope = SeriesScope.OccurrenceOnly;
            if (_sourceIsRecurring)
            {
                if (_selectConversionScope is null)
                {
                    ErrorMessage = "请选择重复提醒的转换范围。";
                    return;
                }

                try
                {
                    var selectedScope = await _selectConversionScope(ct);
                    if (selectedScope is null)
                    {
                        ErrorMessage = "请选择重复提醒的转换范围。";
                        return;
                    }
                    conversionScope = selectedScope.Value;
                }
                catch (Exception exception)
                {
                    ErrorMessage = exception.Message;
                    return;
                }
            }

            await _todoService.ConvertToTodoAsync(
                OccurrenceId, todoDraft!, conversionScope, ct);
            _persistedEditDraft = null;
            EnterRefreshOnly(
                "提醒已转换为待办，但时间轴刷新失败。请仅重试刷新。");
            await FinishRefreshAsync(ct);
        }
        catch (Exception exception)
        {
            ErrorMessage = IsRefreshOnly
                ? $"{_refreshOnlyMessage} {exception.Message}"
                : ordinaryEditAwaitingRefresh
                    ? $"提醒已保存，但时间轴刷新失败。可修改后再次保存，或直接重试保存。 {exception.Message}"
                    : exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBuildTodoDraft(out TodoDraft? draft)
    {
        draft = null;
        var title = Title.Trim();
        if (title.Length == 0)
        {
            ErrorMessage = "请输入提醒内容。";
            return false;
        }
        if (title.Length > 200)
        {
            ErrorMessage = "提醒内容不能超过 200 个字符。";
            return false;
        }
        if (!Enum.IsDefined(SelectedImportance))
        {
            ErrorMessage = "请选择有效的重要性。";
            return false;
        }

        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(DateText))
        {
            if (!DateOnly.TryParseExact(
                    DateText.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                ErrorMessage = "请输入有效的日期，或留空表示无日期。";
                return false;
            }
            dueDate = parsedDate;
        }

        draft = new TodoDraft(title, dueDate, SelectedImportance);
        ErrorMessage = null;
        return true;
    }

    private bool TryBeginOperation()
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            return false;
        NotifyOperationStateChanged();
        return true;
    }

    private void EndOperation()
    {
        Volatile.Write(ref _operationInProgress, 0);
        NotifyOperationStateChanged();
    }

    private void EnterRefreshOnly(string message)
    {
        _refreshOnlyMessage = message;
        NotifyRefreshOnlyChanged();
    }

    private async Task FinishRefreshAsync(CancellationToken ct)
    {
        await _afterSaved(ct);
        _persistedEditDraft = null;
        _refreshOnlyMessage = null;
        ErrorMessage = null;
        NotifyRefreshOnlyChanged();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCancel));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void NotifyRefreshOnlyChanged()
    {
        OnPropertyChanged(nameof(IsRefreshOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PrimaryActionText));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private DateTimeOffset ResolveLocal(DateTime local)
    {
        if (_zone.IsAmbiguousTime(local))
        {
            return _zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset))
                .MinBy(candidate => candidate.UtcDateTime);
        }
        return new DateTimeOffset(local, _zone.GetUtcOffset(local));
    }

    private static ReminderDraft CreateDraft(TimelineItemViewModel item)
    {
        var time = TimeOnly.FromDateTime(item.DueAt.DateTime);
        RecurrenceRule? recurrence = null;
        if (item.RecurrenceText == "每天")
        {
            recurrence = RecurrenceRule.Daily(time);
        }
        else if (item.RecurrenceText?.StartsWith("工作日", StringComparison.Ordinal) == true)
        {
            recurrence = RecurrenceRule.Weekdays(time);
        }
        else if (item.IsRecurring)
        {
            var days = DayLabels
                .Where(pair => item.RecurrenceText?.Contains(pair.Key, StringComparison.Ordinal) == true)
                .Select(pair => pair.Value)
                .ToArray();
            if (days.Length > 0)
                recurrence = RecurrenceRule.Weekly(days, time);
        }

        return new ReminderDraft(item.Title, item.DueAt, item.Kind, item.Importance, recurrence);
    }

    private static bool ReminderDraftEquals(
        ReminderDraft? left,
        SeriesScope leftScope,
        ReminderDraft right,
        SeriesScope rightScope) =>
        left is not null &&
        leftScope == rightScope &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        left.DueAt == right.DueAt &&
        left.Kind == right.Kind &&
        left.Importance == right.Importance &&
        RecurrenceEquals(left.Recurrence, right.Recurrence);

    private static bool RecurrenceEquals(RecurrenceRule? left, RecurrenceRule? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return left.Kind == right.Kind &&
            left.Time == right.Time &&
            left.DaysOfWeek.SetEquals(right.DaysOfWeek);
    }
}
