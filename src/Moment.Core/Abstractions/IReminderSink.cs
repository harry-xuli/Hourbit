using Moment.Core.Domain;

namespace Moment.Core.Abstractions;

public interface IReminderSink
{
    Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct);
    Task DeliverMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct);
}

public interface ISchedulerSignal
{
    void Refresh();
}
