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
    internal ReminderDraft Draft { get; } = choice.Draft as ReminderDraft ??
        throw new ArgumentException("Quick Add choices must be reminder drafts.", nameof(choice));
    public string Label { get; } = choice.Label;
}

public sealed class QuickAddViewModel : ObservableObject
{
    private readonly IChineseTimeParser _parser;
    private readonly IReminderService _service;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Func<CancellationToken, Task> _afterCreated;
    private string _text = string.Empty;
    private string? _previewText;
    private string? _errorMessage;
    private ReminderDraft? _draft;
    private QuickAddState _state;
    private bool _areDetailsVisible;
    private EditReminderViewModel? _details;
    private bool _creationPersisted;

    public QuickAddViewModel(
        IChineseTimeParser parser,
        IReminderService service,
        IClock clock,
        TimeZoneInfo zone,
        Func<CancellationToken, Task>? afterCreated = null)
    {
        _parser = parser;
        _service = service;
        _clock = clock;
        _zone = zone;
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
        private set => SetProperty(ref _areDetailsVisible, value);
    }
    public EditReminderViewModel? Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }
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
        if (Details is null || AreDetailsVisible)
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
                if (!Details.TryBuildDraft(out draft))
                {
                    ErrorMessage = Details.ErrorMessage;
                    return;
                }
            }
            if (!_creationPersisted)
            {
                await _service.CreateAsync(draft!, ct);
                _creationPersisted = true;
            }
            await _afterCreated(ct);
            _creationPersisted = false;
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
        Details = new EditReminderViewModel(_draft, _zone);
        Choices.Clear();
        PreviewText = FormatPreview(_draft);
        ErrorMessage = null;
        State = QuickAddState.Success;
        return Task.CompletedTask;
    }

    private void Parse()
    {
        _draft = null;
        _creationPersisted = false;
        Details = null;
        PreviewText = null;
        ErrorMessage = null;
        Choices.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            State = QuickAddState.Empty;
            return;
        }

        switch (_parser.Parse(Text, _clock.Now, _zone, CultureInfo.CurrentCulture))
        {
            case ParseResult.Success { Draft: ReminderDraft reminderDraft }:
                _draft = reminderDraft;
                Details = new EditReminderViewModel(reminderDraft, _zone);
                PreviewText = FormatPreview(reminderDraft);
                State = QuickAddState.Success;
                break;
            case ParseResult.Success:
                ErrorMessage = "待办事项将在后续界面更新中启用。";
                State = QuickAddState.Invalid;
                break;
            case ParseResult.Ambiguous ambiguous:
                foreach (var choice in ambiguous.Choices)
                    Choices.Add(new QuickAddChoiceViewModel(choice));
                State = QuickAddState.Ambiguous;
                break;
            case ParseResult.Invalid invalid:
                ErrorMessage = invalid.Message;
                State = QuickAddState.Invalid;
                break;
        }
    }

    private string FormatPreview(ReminderDraft draft)
    {
        var local = TimeZoneInfo.ConvertTime(draft.DueAt, _zone);
        var recurrence = draft.Recurrence is null ? "单次" : draft.Recurrence.Kind switch
        {
            RecurrenceKind.Daily => "每天",
            RecurrenceKind.Weekdays => "工作日",
            _ => "每周"
        };
        var importance = draft.Importance == ReminderImportance.Important ? "重要提醒" : "普通提醒";
        return $"{local.Year}年{local.Month}月{local.Day}日 {local:HH:mm} · {recurrence} · {importance}";
    }
}
