using Moment.Core.Abstractions;
using Moment.Core.Domain;

namespace Moment.TestSupport;

public sealed class FakeTodoRepository : ITodoRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TodoItem> _items = [];

    public Task SaveAsync(TodoItem item, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_items.TryAdd(item.Id, item))
                throw new InvalidOperationException("Todo already exists.");
        }
        return Task.CompletedTask;
    }

    public Task<TodoItem?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TodoItem>>(
                _items.Values
                    .OrderBy(static item => item.DueDate is null)
                    .ThenBy(static item => item.DueDate)
                    .ThenBy(static item => item.Id)
                    .ToArray());
        }
    }

    public Task UpdateAsync(TodoItem item, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(item.Id))
                _items[item.Id] = item;
        }
        return Task.CompletedTask;
    }

    public Task SetCompletedAsync(
        Guid id,
        bool isCompleted,
        DateTimeOffset? completedAt,
        CancellationToken ct)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var existing))
            {
                _items[id] = new TodoItem(
                    existing.Id,
                    existing.Title,
                    existing.CreatedAt,
                    existing.DueDate,
                    existing.Importance,
                    isCompleted,
                    completedAt);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
            _items.Remove(id);
        return Task.CompletedTask;
    }
}
