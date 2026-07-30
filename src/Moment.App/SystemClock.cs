using Moment.Core.Abstractions;

namespace Moment.App;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken ct)
    {
        var delay = dueAt - Now;
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
    }
}
