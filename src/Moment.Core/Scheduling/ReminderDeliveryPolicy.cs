namespace Moment.Core.Scheduling;

public sealed class ReminderDeliveryPolicy
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    public DateTimeOffset? GetNextRetryAt(int completedAttempts, DateTimeOffset now) =>
        completedAttempts is >= 1 and <= 3
            ? now.Add(RetryDelays[completedAttempts - 1])
            : null;
}
