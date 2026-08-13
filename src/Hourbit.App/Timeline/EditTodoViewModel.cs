using System.Globalization;
using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public sealed class EditTodoViewModel : ObservableObject
{
    private readonly TimeZoneInfo _zone;
    private readonly ITodoService? _service;
    private IReminderService? _reminderService;
    private readonly Func<CancellationToken, Task> _afterSaved;
    private string _title;
    private string _dateText;
    private string _timeText = string.Empty;
    private ReminderImportance _selectedImportance;
    private string? _errorMessage;
    private TodoDraft? _persistedSaveDraft;
    private string? _refreshOnlyMessage;
    private bool _createMode;
    private int _operationInProgress;

    public EditTodoViewModel(TodoDraft draft, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _title = draft.Title;
        _dateText = FormatDate(draft.DueDate);
        _selectedImportance = draft.Importance;
        _afterSaved = _ => Task.CompletedTask;
        InitializeCommands();
    }

    public EditTodoViewModel(
        TodoDraft draft,
        TimeZoneInfo zone,
        ITodoService service,
        Func<CancellationToken, Task>? afterSaved = null)
        : this(draft, zone)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _afterSaved = afterSaved ?? (_ => Task.CompletedTask);
        _createMode = true;
    }

    public EditTodoViewModel(
        TodoItem item,
        TimeZoneInfo zone,
        ITodoService service,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _afterSaved = afterSaved ?? (_ => Task.CompletedTask);
        TodoId = item.Id;
        IsCompleted = item.IsCompleted;
        _title = item.Title;
        _dateText = FormatDate(item.DueDate);
        _selectedImportance = item.Importance;
        InitializeCommands();
    }

    public event EventHandler? CloseRequested;

    public Guid TodoId { get; }
    public string EditorTitle => LocalizationHub.Translate(
        _createMode ? "Editor.NewTodoCopy" : "Editor.EditTodo");
    public bool IsCompleted { get; }
    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;
    public bool IsRefreshOnly => _refreshOnlyMessage is not null;
    public bool CanEdit => !IsBusy && !IsRefreshOnly;
    public bool CanCancel => CanEdit;
    public string PrimaryActionText => LocalizationHub.Translate(
        IsRefreshOnly ? "Editor.RetryRefresh" : "Editor.Save");

    public IReadOnlyList<EditOption<ReminderImportance>> Importances { get; } =
    [
        new(ReminderImportance.Normal, LocalizationHub.Translate("Importance.Normal")),
        new(ReminderImportance.Important, LocalizationHub.Translate("Importance.Important"))
    ];

    public IAsyncCommand SaveCommand { get; private set; } = null!;
    public IAsyncCommand CompleteCommand { get; private set; } = null!;
    public IAsyncCommand DeleteCommand { get; private set; } = null!;

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
                OnPropertyChanged(nameof(ConvertsToReminder));
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

    public bool ConvertsToReminder => !string.IsNullOrWhiteSpace(TimeText);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public static EditTodoViewModel CreateCopy(
        TodoItem source,
        TimeZoneInfo zone,
        ITodoService service,
        IReminderService reminderService,
        Func<CancellationToken, Task>? afterSaved = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var viewModel = new EditTodoViewModel(
            new TodoDraft(source.Title, source.DueDate, source.Importance),
            zone,
            service,
            afterSaved);
        viewModel._reminderService = reminderService ??
            throw new ArgumentNullException(nameof(reminderService));
        return viewModel;
    }

    public bool TryBuildDraft(out ItemDraft? draft)
    {
        draft = null;
        var title = Title.Trim();
        if (title.Length == 0)
        {
            ErrorMessage = "请输入待办内容。";
            return false;
        }
        if (title.Length > 200)
        {
            ErrorMessage = "待办内容不能超过 200 个字符。";
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

        if (string.IsNullOrWhiteSpace(TimeText))
        {
            draft = new TodoDraft(title, dueDate, SelectedImportance);
            ErrorMessage = null;
            return true;
        }

        if (dueDate is null)
        {
            ErrorMessage = "添加时间时请同时选择日期。";
            return false;
        }
        if (!TimeOnly.TryParseExact(
                TimeText.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            ErrorMessage = "请输入有效的时间，格式为 HH:mm。";
            return false;
        }

        var localDue = dueDate.Value.ToDateTime(time, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(localDue))
        {
            ErrorMessage = "该本地时间不存在，请选择其他时间。";
            return false;
        }

        draft = new ReminderDraft(
            title,
            ResolveLocal(localDue),
            ReminderKind.Plan,
            SelectedImportance,
            null);
        ErrorMessage = null;
        return true;
    }

    public Task SaveAsync() => SaveAsync(CancellationToken.None);

    public async Task SaveAsync(CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("This todo editor is not connected to persistence.");
        if (!TryBeginOperation())
            return;
        var ordinarySaveAwaitingRefresh = false;
        try
        {
            if (IsRefreshOnly)
            {
                await FinishRefreshAsync(ct);
                return;
            }
            if (!TryBuildDraft(out var draft))
                return;

            switch (draft)
            {
                case TodoDraft todoDraft:
                    if (_createMode)
                    {
                        await _service.CreateAsync(todoDraft, ct);
                        EnterRefreshOnly(
                            "待办副本已创建，但时间轴刷新失败。请仅重试刷新。");
                        break;
                    }
                    if (!TodoDraftEquals(_persistedSaveDraft, todoDraft))
                    {
                        await _service.EditAsync(TodoId, todoDraft, ct);
                        _persistedSaveDraft = todoDraft;
                    }
                    ordinarySaveAwaitingRefresh = true;
                    break;
                case ReminderDraft reminderDraft:
                    if (_createMode)
                    {
                        if (_reminderService is null)
                            throw new InvalidOperationException(
                                "Reminder copy service is not connected.");
                        await _reminderService.CreateAsync(reminderDraft, ct);
                        EnterRefreshOnly(
                            "提醒副本已创建，但时间轴刷新失败。请仅重试刷新。");
                        break;
                    }
                    await _service.ConvertToReminderAsync(TodoId, reminderDraft, ct);
                    _persistedSaveDraft = null;
                    EnterRefreshOnly(
                        "待办已转换为提醒，但时间轴刷新失败。请仅重试刷新。");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported todo edit result.");
            }
            await FinishRefreshAsync(ct);
        }
        catch (Exception exception)
        {
            ReportOperationFailure(
                exception,
                ordinarySaveAwaitingRefresh
                    ? "待办已保存，但时间轴刷新失败。可修改后再次保存，或直接重试保存。"
                    : null);
        }
        finally
        {
            EndOperation();
        }
    }

    public Task CompleteAsync() => CompleteAsync(CancellationToken.None);

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("This todo editor is not connected to persistence.");
        if (IsCompleted)
            return;
        if (!TryBeginOperation())
            return;
        try
        {
            if (IsRefreshOnly)
                return;
            await _service.CompleteAsync(TodoId, ct);
            _persistedSaveDraft = null;
            EnterRefreshOnly("待办已完成，但时间轴刷新失败。请仅重试刷新。");
            await FinishRefreshAsync(ct);
        }
        catch (Exception exception)
        {
            ReportOperationFailure(exception, null);
        }
        finally
        {
            EndOperation();
        }
    }

    public Task DeleteAsync() => DeleteAsync(CancellationToken.None);

    public async Task DeleteAsync(CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("This todo editor is not connected to persistence.");
        if (!TryBeginOperation())
            return;
        try
        {
            if (IsRefreshOnly)
                return;
            await _service.DeleteAsync(TodoId, ct);
            _persistedSaveDraft = null;
            EnterRefreshOnly("待办已删除，但时间轴刷新失败。请仅重试刷新。");
            await FinishRefreshAsync(ct);
        }
        catch (Exception exception)
        {
            ReportOperationFailure(exception, null);
        }
        finally
        {
            EndOperation();
        }
    }

    private void InitializeCommands()
    {
        SaveCommand = new AsyncCommand((_, ct) => SaveAsync(ct), _ => !IsBusy);
        CompleteCommand = new AsyncCommand(
            (_, ct) => CompleteAsync(ct), _ => !IsCompleted && CanEdit);
        DeleteCommand = new AsyncCommand((_, ct) => DeleteAsync(ct), _ => CanEdit);
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
        _persistedSaveDraft = null;
        _refreshOnlyMessage = null;
        ErrorMessage = null;
        NotifyRefreshOnlyChanged();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ReportOperationFailure(Exception exception, string? savedMessage)
    {
        ErrorMessage = IsRefreshOnly
            ? $"{_refreshOnlyMessage} {exception.Message}"
            : savedMessage is not null
                ? $"{savedMessage} {exception.Message}"
                : exception.Message;
    }

    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCancel));
        RaiseOperationCanExecuteChanged();
    }

    private void NotifyRefreshOnlyChanged()
    {
        OnPropertyChanged(nameof(IsRefreshOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PrimaryActionText));
        RaiseOperationCanExecuteChanged();
    }

    private void RaiseOperationCanExecuteChanged()
    {
        SaveCommand.RaiseCanExecuteChanged();
        CompleteCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
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

    private static string FormatDate(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TodoDraftEquals(TodoDraft? left, TodoDraft right) =>
        left is not null &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        left.DueDate == right.DueDate &&
        left.Importance == right.Importance;
}
