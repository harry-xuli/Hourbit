using Hourbit.Core.Domain;

namespace Hourbit.Core.Scheduling;

public interface IReminderRecoverySummarySink
{
    Task SendMissedSummaryAsync(
        IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct);
}
