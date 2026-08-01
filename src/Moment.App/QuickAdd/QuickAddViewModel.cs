using System.Collections.ObjectModel;
using System.Globalization;
using Moment.App.Commands;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;

namespace Moment.App.QuickAdd;

public enum QuickAddState { Empty, Success, Ambiguous, Invalid }

public sealed class QuickAddChoiceViewModel(ParseChoice choice)
{
    internal ItemDraft Draft { get; } = choice.Draft;
    public string Label { get; } = choice.Label;
}

public sealed class QuickAddViewModel : ObservableObject
{
    private readonly IChineseTimeParser _parser;
    private readonly IReminderService _reminderService;
    private readonly ITodoService _todoService;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly Func<CancellationToken, Task> _afterCreated;
    private string _text = string.Empty;
    private string? _previewText;
    private string? _errorMessage;
    private ItemDraft? _draft;
    private QuickAddState _state;
    private bool _areDetailsVisible;
    private EditReminderViewModel? _details;
    private EditTodoViewModel? _todoDetails;
    private ItemDraft? _persistedDraft;

    public QuickAddViewModel(
        IChineseTimeParser parser,
        IReminderService reminderService,
        ITodoService todoService,
        IClock clock,
        TimeZoneInfo zone,
        CultureInfo culture,
        Func<CancellationToken, Task>? afterCreated = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _reminderService = reminderService ??
            throw new ArgumentNullException(nameof(reminderService));
        _todoService = todoService ?? throw new ArgumentNullException(nameof(todoService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _culture = culture ?? throw new ArgumentNullException(nameof(culture));
        _afterCreated = afterCreated ?? (_ => Task.CompletedTask);
        SubmitCommand = new AsyncCommand((_, ct) => SubmitAsync(ct));
        ChooseCommand = new AsyncCommand((choice, _) =>
            choice is QuickAddChoiceViewModel value ? ChooseAsync(value) : Task.CompletedTask);
        ToggleDetailsCommand = new AsyncCommand((_, _) =>
        {
            AreDetailsVisible = !AreDetailsVisible;
            return Task.CompletedTask;
        });
        HideCommand = new AsyncCommand((_, _) =>
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
    }

    public event EventHandler? HideRequested;
    public ObservableCollection<QuickAddChoiceViewModel> Choices { get; } = [];
    public IAsyncCommand SubmitCommand { get; }
    public IAsyncCommand ChooseCommand { get; }
    public IAsyncCommand ToggleDetailsCommand { get; }
    public IAsyncCommand HideCommand { get; }
    public QuickAddState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsChoicePanelVisible));
        }
    }
    public bool IsChoicePanelVisible => State == QuickAddState.Ambiguous;
    public string GuidanceText => "请选择具体时间";
    public string FooterText => "Enter 创建 · Tab 更多选项 · Esc 隐藏";
    public string? PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }
    public bool AreDetailsVisible
    {
        get => _areDetailsVisible;
        private set
        {
            if (!SetProperty(ref _areDetailsVisible, value))
                return;
            OnPropertyChanged(nameof(IsReminderDetailsVisible));
            OnPropertyChanged(nameof(IsTodoDetailsVisible));
        }
    }
    public EditReminderViewModel? Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }
    public EditTodoViewModel? TodoDetails
    {
        get => _todoDetails;
        private set => SetProperty(ref _todoDetails, value);
    }
    public bool IsReminderDetailsVisible => AreDetailsVisible && Details is not null;
    public bool IsTodoDetailsVisible => AreDetailsVisible && TodoDetails is not null;
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? string.Empty))
                Parse();
        }
    }

    public bool ShowDetails()
    {
        if ((Details is null && TodoDetails is null) || AreDetailsVisible)
            return false;
        AreDetailsVisible = true;
        return true;
    }

    public Task SubmitAsync() => SubmitAsync(CancellationToken.None);

    public async Task SubmitAsync(CancellationToken ct)
    {
        if (_draft is null || State != QuickAddState.Success)
            return;
        try
        {
            ErrorMessage = null;
            var draft = _draft;
            if (AreDetailsVisible && Details is not null)
            {
                if (!Details.TryBuildDraft(out var reminderDraft))
                {
                    ErrorMessage = Details.ErrorMessage;
                    return;
                }
                draft = reminderDraft;
            }
            else if (AreDetailsVisible && TodoDetails is not null)
            {
                if (!TodoDetails.TryBuildDraft(out draft))
                {
                    ErrorMessage = TodoDetails.ErrorMessage;
                    return;
                }
            }
            if (!Equals(_persistedDraft, draft))
            {
                switch (draft)
                {
                    case ReminderDraft reminderDraft:
                        await _reminderService.CreateAsync(reminderDraft, ct);
                        break;
                    case TodoDraft todoDraft:
                        await _todoService.CreateAsync(todoDraft, ct);
                        break;
                    default:
                        throw new InvalidOperationException("不支持的快速创建类型。");
                }
                _persistedDraft = draft;
            }
            await _afterCreated(ct);
            _persistedDraft = null;
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    public Task ChooseAsync(QuickAddChoiceViewModel choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        _draft = choice.Draft;
        SetDetails(_draft);
        Choices.Clear();
        PreviewText = FormatPreview(_draft);
        ErrorMessage = null;
        State = QuickAddState.Success;
        return Task.CompletedTask;
    }

    private void Parse()
    {
        _draft = null;
        _persistedDraft = null;
        Details = null;
        TodoDetails = null;
        PreviewText = null;
        ErrorMessage = null;
        Choices.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            AreDetailsVisible = false;
            State = QuickAddState.Empty;
            return;
        }

        switch (_parser.Parse(Text, _clock.Now, _zone, _culture))
        {
            case ParseResult.Success { Draft: ReminderDraft reminderDraft }:
                _draft = reminderDraft;
                SetDetails(reminderDraft);
                PreviewText = FormatPreview(reminderDraft);
                State = QuickAddState.Success;
                break;
            case ParseResult.Success { Draft: TodoDraft todoDraft }:
                _draft = todoDraft;
                SetDetails(todoDraft);
                PreviewText = FormatPreview(todoDraft);
                State = QuickAddState.Success;
                break;
            case ParseResult.Ambiguous ambiguous:
                AreDetailsVisible = false;
                foreach (var choice in ambiguous.Choices)
                    Choices.Add(new QuickAddChoiceViewModel(choice));
                State = QuickAddState.Ambiguous;
                break;
            case ParseResult.Invalid invalid:
                AreDetailsVisible = false;
                ErrorMessage = invalid.Message;
                State = QuickAddState.Invalid;
                break;
        }
    }

    private void SetDetails(ItemDraft draft)
    {
        Details = draft is ReminderDraft reminderDraft
            ? new EditReminderViewModel(reminderDraft, _zone)
            : null;
        TodoDetails = draft is TodoDraft todoDraft
            ? new EditTodoViewModel(todoDraft, _zone)
            : null;
        OnPropertyChanged(nameof(IsReminderDetailsVisible));
        OnPropertyChanged(nameof(IsTodoDetailsVisible));
    }

    private string FormatPreview(ItemDraft draft) => draft switch
    {
        TodoDraft { DueDate: null } => "待办 · 无日期",
        TodoDraft todo => $"待办 · 截止 {todo.DueDate!.Value.ToString(
            "yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        ReminderDraft reminder => FormatReminderPreview(reminder),
        _ => throw new ArgumentOutOfRangeException(nameof(draft))
    };

    private string FormatReminderPreview(ReminderDraft draft)
    {
        var local = TimeZoneInfo.ConvertTime(draft.DueAt, _zone);
        return $"提醒 · {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
    }
}
