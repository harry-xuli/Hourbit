namespace Moment.Core.Abstractions;

public interface IClock
{
    DateTimeOffset Now { get; }
    Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken ct);
}
