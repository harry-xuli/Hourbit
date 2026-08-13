using Moment.Core.Domain;

namespace Moment.Core.Search;

public enum SearchItemType
{
    Reminder,
    Todo,
}

public sealed record ItemSearchFilter
{
    public ItemSearchFilter(string text)
    {
        Text = text?.Trim() ?? string.Empty;
    }

    public string Text { get; }
}

public sealed record ItemSearchResult(
    Guid Id,
    SearchItemType Type,
    string Title,
    DateOnly? LocalDate,
    ReminderImportance Importance,
    bool IsCompleted);

public interface IItemSearchQuery
{
    Task<IReadOnlyList<ItemSearchResult>> SearchAsync(
        ItemSearchFilter filter,
        CancellationToken ct);
}
