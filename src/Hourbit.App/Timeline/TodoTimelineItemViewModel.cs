using System.Globalization;
using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.Core.Domain;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public sealed class TodoTimelineItemViewModel : ObservableObject
{
    private UiLanguage _language;

    public TodoTimelineItemViewModel(
        TodoTimelineRow row,
        DateOnly localDate,
        UiLanguage language = UiLanguage.ZhCn)
    {
        TodoId = row.TodoId;
        Title = row.Title;
        CreatedAt = row.CreatedAt;
        DueDate = row.DueDate;
        Importance = row.Importance;
        IsCompleted = row.IsCompleted;
        CompletedAt = row.CompletedAt;
        _language = language;
        IsOverdue = !IsCompleted && DueDate is { } dueDate && dueDate < localDate;
        DueOrder = DueDate switch
        {
            { } pastDueDate when pastDueDate < localDate => 0,
            { } todayDueDate when todayDueDate == localDate => 1,
            not null => 2,
            null => 3
        };
    }

    public Guid TodoId { get; }
    public string Title { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateOnly? DueDate { get; }
    public ReminderImportance Importance { get; }
    public bool IsCompleted { get; }
    public DateTimeOffset? CompletedAt { get; }
    public bool IsOverdue { get; }
    public int DueOrder { get; }
    public string DueDateText => DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        ?? Translate("Timeline.NoDate");
    public string ImportanceText => Translate(
        Importance == ReminderImportance.Important
            ? "Importance.Important"
            : "Importance.Normal");
    public string ImportanceSymbol => Importance == ReminderImportance.Important ? "↑" : "●";
    public string StatusText => Translate(IsCompleted
        ? "Timeline.Status.Completed"
        : IsOverdue
            ? "Timeline.TodoOverdue"
            : "Timeline.Todo");
    public string StatusSymbol => IsCompleted ? "✓" : IsOverdue ? "!" : "□";
    public string? CompletedTimeText => CompletedAt?.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string AccessibleName =>
        $"{Translate("Timeline.Todo")}：{Title}，{DueDateText}，{ImportanceText}，{StatusText}";

    public TodoItem Item => new(
        TodoId,
        Title,
        CreatedAt,
        DueDate,
        Importance,
        IsCompleted,
        CompletedAt);

    public void SetLanguage(UiLanguage language)
    {
        if (_language == language)
            return;
        _language = language;
        OnPropertyChanged(nameof(DueDateText));
        OnPropertyChanged(nameof(ImportanceText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AccessibleName));
    }

    private string Translate(string key) =>
        LocalizationCatalog.Translate(_language, key);
}
