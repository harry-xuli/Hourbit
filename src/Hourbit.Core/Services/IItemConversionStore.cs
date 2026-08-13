using Hourbit.Core.Domain;

namespace Hourbit.Core.Services;

public sealed record TodoToReminderConversion(
    TodoItem Source,
    ReminderItem DestinationItem,
    ReminderOccurrence DestinationOccurrence);

public sealed record ReminderToTodoConversion(
    ScheduledReminder Source,
    TodoItem Destination,
    SeriesScope Scope,
    ReminderOccurrence? ContinuationOccurrence);

public sealed record ItemConversionResult(bool SchedulingChanged);

public interface IItemConversionStore
{
    Task<ItemConversionResult> ConvertTodoToReminderAsync(
        TodoToReminderConversion request,
        CancellationToken ct);

    Task<ItemConversionResult> ConvertReminderToTodoAsync(
        ReminderToTodoConversion request,
        CancellationToken ct);
}
