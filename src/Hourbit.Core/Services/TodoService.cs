using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Recurrence;

namespace Hourbit.Core.Services;

public interface ITodoService
{
    Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct);
    Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct);
    Task CompleteAsync(Guid todoId, CancellationToken ct);
    Task DeleteAsync(Guid todoId, CancellationToken ct);
    Task ConvertToReminderAsync(
        Guid todoId,
        ReminderDraft draft,
        CancellationToken ct);
    Task ConvertToTodoAsync(
        Guid occurrenceId,
        TodoDraft draft,
        CancellationToken ct);
    Task ConvertToTodoAsync(
        Guid occurrenceId,
        TodoDraft draft,
        SeriesScope scope,
        CancellationToken ct);
}

public sealed class TodoService(
    ITodoRepository todoRepository,
    IReminderRepository reminderRepository,
    IItemConversionStore conversionStore,
    IRecurrenceCalculator recurrenceCalculator,
    ISchedulerSignal schedulerSignal,
    IClock clock,
    TimeZoneInfo schedulingTimeZone) : ITodoService
{
    private readonly TimeZoneInfo _schedulingTimeZone = schedulingTimeZone ??
        throw new ArgumentNullException(nameof(schedulingTimeZone));

    public async Task<TodoItem> CreateAsync(
        TodoDraft draft,
        CancellationToken ct)
    {
        ValidateTodoDraft(draft);
        var item = new TodoItem(
            Guid.NewGuid(), draft.Title, clock.Now, draft.DueDate,
            draft.Importance, false, null, draft.Recurrence);
        await todoRepository.SaveAsync(item, ct);
        return item;
    }

    public async Task EditAsync(
        Guid todoId,
        TodoDraft draft,
        CancellationToken ct)
    {
        ValidateTodoDraft(draft);
        var current = await todoRepository.GetAsync(todoId, ct);
        if (current is null)
            return;

        var edited = new TodoItem(
            current.Id, draft.Title, current.CreatedAt, draft.DueDate,
            draft.Importance, current.IsCompleted, current.CompletedAt,
            draft.Recurrence);
        await todoRepository.UpdateAsync(edited, ct);
    }

    public async Task CompleteAsync(Guid todoId, CancellationToken ct)
    {
        var current = await todoRepository.GetAsync(todoId, ct);
        if (current is null || current.IsCompleted)
            return;

        var now = clock.Now;
        await todoRepository.SetCompletedAsync(todoId, true, now, ct);

        if (current.Recurrence is { } recurrence)
        {
            var nextDueDate = NextTodoDueDate(current.DueDate, recurrence);
            var next = new TodoItem(
                Guid.NewGuid(), current.Title, now, nextDueDate,
                current.Importance, false, null, current.Recurrence);
            await todoRepository.SaveAsync(next, ct);
        }
    }

    public Task DeleteAsync(Guid todoId, CancellationToken ct) =>
        todoRepository.DeleteAsync(todoId, clock.Now, ct);

    public async Task ConvertToReminderAsync(
        Guid todoId,
        ReminderDraft draft,
        CancellationToken ct)
    {
        var now = clock.Now;
        ValidateReminderDraft(draft);
        var source = await todoRepository.GetAsync(todoId, ct);
        if (source is null)
            return;
        if (!source.IsCompleted && draft.DueAt < now)
            throw new ArgumentOutOfRangeException(nameof(draft));

        var item = new ReminderItem(
            Guid.NewGuid(), draft.Title.Trim(), draft.Kind,
            draft.Importance, source.CreatedAt, draft.Recurrence);
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), item.Id, draft.DueAt,
            source.IsCompleted
                ? OccurrenceState.Completed
                : OccurrenceState.Scheduled,
            source.CompletedAt,
            null);

        var result = await conversionStore.ConvertTodoToReminderAsync(
            new TodoToReminderConversion(source, item, occurrence), ct);
        RefreshSchedulerIfChanged(result);
    }

    public Task ConvertToTodoAsync(
        Guid occurrenceId,
        TodoDraft draft,
        CancellationToken ct) =>
        ConvertToTodoAsync(
            occurrenceId, draft, SeriesScope.OccurrenceOnly, ct);

    public async Task ConvertToTodoAsync(
        Guid occurrenceId,
        TodoDraft draft,
        SeriesScope scope,
        CancellationToken ct)
    {
        ValidateTodoDraft(draft);
        ValidateScope(scope);
        var source = await reminderRepository.GetScheduledReminderAsync(
            occurrenceId, ct);
        if (source is null)
            return;

        var isCompleted =
            source.Occurrence.State == OccurrenceState.Completed;
        var destination = new TodoItem(
            Guid.NewGuid(), draft.Title, source.Item.CreatedAt,
            draft.DueDate, draft.Importance, isCompleted,
            isCompleted ? source.Occurrence.HandledAt : null);

        ReminderOccurrence? continuation = null;
        if (scope == SeriesScope.OccurrenceOnly &&
            source.Item.Recurrence is not null &&
            source.Occurrence.State is
                OccurrenceState.Scheduled or OccurrenceState.Fired)
        {
            continuation = ReminderOccurrence.Schedule(
                source.Item.Id,
                recurrenceCalculator.NextAfter(
                    source.Item.Recurrence,
                    source.Occurrence.DueAt,
                    _schedulingTimeZone));
        }

        var result = await conversionStore.ConvertReminderToTodoAsync(
            new ReminderToTodoConversion(
                source, destination, scope, continuation), ct);
        RefreshSchedulerIfChanged(result);
    }

    private void RefreshSchedulerIfChanged(ItemConversionResult result)
    {
        if (result.SchedulingChanged)
            schedulerSignal.Refresh();
    }

    private static void ValidateTodoDraft(TodoDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = draft.Title?.Trim();
        if (title is null || title.Length is 0 or > 200 ||
            !Enum.IsDefined(draft.Importance))
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        if (draft.Recurrence is not null)
            ValidateRecurrence(draft.Recurrence);
    }

    private DateOnly NextTodoDueDate(
        DateOnly? current,
        RecurrenceRule recurrence)
    {
        var from = current ?? LocalToday;
        for (var offset = 1; offset <= 14; offset++)
        {
            var candidate = from.AddDays(offset);
            if (AllowsTodoDay(recurrence, candidate.DayOfWeek))
                return candidate;
        }

        throw new InvalidOperationException(
            "No recurring todo occurrence found within 14 days.");
    }

    private static bool AllowsTodoDay(
        RecurrenceRule recurrence,
        DayOfWeek dayOfWeek) => recurrence.Kind switch
    {
        RecurrenceKind.Daily => true,
        RecurrenceKind.Weekdays => dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        RecurrenceKind.Weekly => recurrence.DaysOfWeek.Contains(dayOfWeek),
        _ => throw new ArgumentOutOfRangeException(
            nameof(recurrence), recurrence.Kind, "Unknown recurrence kind.")
    };

    private DateOnly LocalToday => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(clock.Now, _schedulingTimeZone).DateTime);

    private static void ValidateReminderDraft(ReminderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = draft.Title?.Trim();
        if (title is null || title.Length is 0 or > 200 ||
            !Enum.IsDefined(draft.Kind) ||
            !Enum.IsDefined(draft.Importance))
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        if (draft.Recurrence is not null)
            ValidateRecurrence(draft.Recurrence);
    }

    private static void ValidateRecurrence(RecurrenceRule recurrence)
    {
        if (!Enum.IsDefined(recurrence.Kind) ||
            recurrence.DaysOfWeek is null ||
            recurrence.DaysOfWeek.Any(day => !Enum.IsDefined(day)))
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence));
        }

        var isValid = recurrence.Kind switch
        {
            RecurrenceKind.Daily or RecurrenceKind.Weekdays =>
                recurrence.DaysOfWeek.Count == 0,
            RecurrenceKind.Weekly => recurrence.DaysOfWeek.Count > 0,
            _ => false
        };
        if (!isValid)
            throw new ArgumentOutOfRangeException(nameof(recurrence));
    }

    private static void ValidateScope(SeriesScope scope)
    {
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope));
    }
}
