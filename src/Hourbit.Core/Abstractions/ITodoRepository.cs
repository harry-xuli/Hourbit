using Hourbit.Core.Domain;

namespace Hourbit.Core.Abstractions;

public interface ITodoRepository
{
    Task SaveAsync(TodoItem item, CancellationToken ct);
    Task<TodoItem?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct);
    Task UpdateAsync(TodoItem item, CancellationToken ct);
    Task SetCompletedAsync(
        Guid id,
        bool isCompleted,
        DateTimeOffset? completedAt,
        CancellationToken ct);
    Task DeleteAsync(
        Guid id,
        DateTimeOffset deletedAt,
        CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct) =>
        DeleteAsync(id, DateTimeOffset.UtcNow, ct);
}
