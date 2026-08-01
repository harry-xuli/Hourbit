using System.Globalization;
using Moment.App.Commands;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;

namespace Moment.App.Timeline;

public sealed class EditTodoViewModel : ObservableObject
{
    private readonly TimeZoneInfo _zone;
    private readonly ITodoService? _service;
    private readonly Func<CancellationToken, Task> _afterSaved;
    private string _title;
    private string _dateText;
    private string _timeText = string.Empty;
    private ReminderImportance _selectedImportance;
    private string? _errorMessage;
    private bool _persistenceCompleted;

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
    public bool IsCompleted { get; }

    public IReadOnlyList<EditOption<ReminderImportance>> Importances { get; } =
    [
        new(ReminderImportance.Normal, "普通"),
        new(ReminderImportance.Important, "重要")
    ];

    public IAsyncCommand SaveCommand { get; private set; } = null!;
    public IAsyncCommand CompleteCommand { get; private set; } = null!;
    public IAsyncCommand DeleteCommand { get; private set; } = null!;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public string DateText
    {
        get => _dateText;
        set => SetProperty(ref _dateText, value ?? string.Empty);
    }

    public string TimeText
    {
        get => _timeText;
        set
        {
            if (SetProperty(ref _timeText, value ?? string.Empty))
                OnPropertyChanged(nameof(ConvertsToReminder));
        }
    }

    public ReminderImportance SelectedImportance
    {
        get => _selectedImportance;
        set => SetProperty(ref _selectedImportance, value);
    }

    public bool ConvertsToReminder => !string.IsNullOrWhiteSpace(TimeText);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
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
        if (!TryBuildDraft(out var draft))
            return;

        await PersistAndCloseAsync(async token =>
        {
            switch (draft)
            {
                case TodoDraft todoDraft:
                    await _service.EditAsync(TodoId, todoDraft, token);
                    break;
                case ReminderDraft reminderDraft:
                    await _service.ConvertToReminderAsync(TodoId, reminderDraft, token);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported todo edit result.");
            }
        }, ct);
    }

    public Task CompleteAsync() => CompleteAsync(CancellationToken.None);

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("This todo editor is not connected to persistence.");
        if (IsCompleted)
            return;
        await PersistAndCloseAsync(token => _service.CompleteAsync(TodoId, token), ct);
    }

    public Task DeleteAsync() => DeleteAsync(CancellationToken.None);

    public async Task DeleteAsync(CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("This todo editor is not connected to persistence.");
        await PersistAndCloseAsync(token => _service.DeleteAsync(TodoId, token), ct);
    }

    private void InitializeCommands()
    {
        SaveCommand = new AsyncCommand((_, ct) => SaveAsync(ct));
        CompleteCommand = new AsyncCommand((_, ct) => CompleteAsync(ct), _ => !IsCompleted);
        DeleteCommand = new AsyncCommand((_, ct) => DeleteAsync(ct));
    }

    private async Task PersistAndCloseAsync(
        Func<CancellationToken, Task> persist,
        CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            if (!_persistenceCompleted)
            {
                await persist(ct);
                _persistenceCompleted = true;
            }
            await _afterSaved(ct);
            _persistenceCompleted = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
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
}
