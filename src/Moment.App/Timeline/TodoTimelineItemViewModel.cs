using System.Globalization;
using Moment.App.Commands;
using Moment.Core.Domain;
using Moment.Core.Services;

namespace Moment.App.Timeline;

public sealed class TodoTimelineItemViewModel : ObservableObject
{
    public TodoTimelineItemViewModel(TodoTimelineRow row, DateOnly localDate)
    {
        TodoId = row.TodoId;
        Title = row.Title;
        CreatedAt = row.CreatedAt;
        DueDate = row.DueDate;
        Importance = row.Importance;
        IsCompleted = row.IsCompleted;
        CompletedAt = row.CompletedAt;
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
        ?? "无日期";
    public string ImportanceText => Importance == ReminderImportance.Important ? "重要" : "普通";
    public string ImportanceSymbol => Importance == ReminderImportance.Important ? "↑" : "●";
    public string StatusText => IsCompleted ? "已完成" : IsOverdue ? "已逾期" : "待办中";
    public string StatusSymbol => IsCompleted ? "✓" : IsOverdue ? "!" : "□";
    public string? CompletedTimeText => CompletedAt?.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string AccessibleName =>
        $"待办：{Title}，{DueDateText}，{ImportanceText}，{StatusText}";

    public TodoItem Item => new(
        TodoId,
        Title,
        CreatedAt,
        DueDate,
        Importance,
        IsCompleted,
        CompletedAt);
}
