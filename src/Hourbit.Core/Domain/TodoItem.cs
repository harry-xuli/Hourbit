namespace Hourbit.Core.Domain;

public sealed record TodoItem
{
    public TodoItem(
        Guid Id,
        string Title,
        DateTimeOffset CreatedAt,
        DateOnly? DueDate,
        ReminderImportance Importance,
        bool IsCompleted,
        DateTimeOffset? CompletedAt,
        RecurrenceRule? Recurrence = null)
    {
        ArgumentNullException.ThrowIfNull(Title);
        var normalizedTitle = Title.Trim();
        if (normalizedTitle.Length is 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(Title));
        if (IsCompleted != CompletedAt.HasValue)
        {
            throw new ArgumentException(
                "Completed todos require a completion timestamp, and pending todos cannot have one.",
                nameof(CompletedAt));
        }
        if (CompletedAt < CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(CompletedAt));

        this.Id = Id;
        this.Title = normalizedTitle;
        this.CreatedAt = CreatedAt;
        this.DueDate = DueDate;
        this.Importance = Importance;
        this.IsCompleted = IsCompleted;
        this.CompletedAt = CompletedAt;
        this.Recurrence = Recurrence;
    }

    public Guid Id { get; }
    public string Title { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateOnly? DueDate { get; }
    public ReminderImportance Importance { get; }
    public bool IsCompleted { get; }
    public DateTimeOffset? CompletedAt { get; }
    public RecurrenceRule? Recurrence { get; }
}
