namespace Hourbit.Core.Abstractions;

public interface ITodoOrderStore
{
    Task<IReadOnlyList<Guid>> LoadAsync(CancellationToken ct);

    Task SaveAsync(IReadOnlyList<Guid> todoIds, CancellationToken ct);
}

public sealed class NullTodoOrderStore : ITodoOrderStore
{
    public Task<IReadOnlyList<Guid>> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    public Task SaveAsync(IReadOnlyList<Guid> todoIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
