using System.Globalization;
using Moment.Core.Abstractions;

namespace Moment.TestSupport;

public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private readonly List<PendingDelay> _pending = [];
    private DateTimeOffset _now;

    public FakeClock(string now) : this(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture))
    {
    }

    public FakeClock(DateTimeOffset now) => _now = now;

    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (dueAt <= _now)
            {
                return Task.CompletedTask;
            }

            var pending = new PendingDelay(dueAt, ct);
            _pending.Add(pending);
            pending.Registration = ct.Register(() => Cancel(pending));
            return pending.Source.Task;
        }
    }

    public void AdvanceBy(TimeSpan duration)
    {
        PendingDelay[] completed;
        lock (_gate)
        {
            _now = _now.Add(duration);
            completed = _pending.Where(pending => pending.DueAt <= _now).ToArray();
            _pending.RemoveAll(pending => pending.DueAt <= _now);
        }

        foreach (var pending in completed)
        {
            pending.Registration.Dispose();
            pending.Source.TrySetResult();
        }
    }

    private void Cancel(PendingDelay pending)
    {
        lock (_gate)
        {
            _pending.Remove(pending);
        }

        pending.Source.TrySetCanceled(pending.CancellationToken);
    }

    private sealed class PendingDelay(DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        public DateTimeOffset DueAt { get; } = dueAt;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
}
