using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Scheduling;

namespace Moment.TestSupport;

public sealed class RecordingReminderSink : IReminderSink, IReminderRecoverySummarySink
{
    private readonly object _gate = new();
    private readonly List<ScheduledReminder> _deliveries = [];
    private readonly List<IReadOnlyList<ScheduledReminder>> _missedSummaries = [];
    private TaskCompletionSource _changed = NewSignal();

    public IReadOnlyList<ScheduledReminder> Deliveries
    {
        get
        {
            lock (_gate)
            {
                return _deliveries.ToArray();
            }
        }
    }

    public IReadOnlyList<IReadOnlyList<ScheduledReminder>> MissedSummaries
    {
        get
        {
            lock (_gate)
            {
                return _missedSummaries.Select(summary => (IReadOnlyList<ScheduledReminder>)summary.ToArray()).ToArray();
            }
        }
    }

    public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(() => _deliveries.Add(reminder));
        return Task.CompletedTask;
    }

    public Task DeliverMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)
        => SendMissedSummaryAsync(reminders, ct);

    public Task SendMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(() => _missedSummaries.Add(reminders.ToArray()));
        return Task.CompletedTask;
    }

    public async Task WaitForCountAsync(int count, CancellationToken ct = default)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_deliveries.Count >= count)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Record(Action record)
    {
        lock (_gate)
        {
            record();
            _changed.TrySetResult();
            _changed = NewSignal();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
