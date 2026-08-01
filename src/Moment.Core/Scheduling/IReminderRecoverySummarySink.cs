using Moment.Core.Domain;

namespace Moment.Core.Scheduling;

public interface IReminderRecoverySummarySink
{
    Task SendMissedSummaryAsync(
        IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct);
}
