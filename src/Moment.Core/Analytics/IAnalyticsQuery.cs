namespace Moment.Core.Analytics;

public interface IAnalyticsQuery
{
    Task<AnalyticsHistory> ReadAsync(
        DateTimeOffset utcStartInclusive,
        DateTimeOffset utcEndExclusive,
        bool includeDeleted,
        CancellationToken ct);
}
