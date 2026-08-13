namespace Hourbit.App.Timeline;

public interface IDatePicker
{
    Task<DateOnly?> ChooseAsync(DateOnly current, CancellationToken ct);
}

public sealed class NullDatePicker : IDatePicker
{
    public Task<DateOnly?> ChooseAsync(DateOnly current, CancellationToken ct) =>
        Task.FromResult<DateOnly?>(null);
}
