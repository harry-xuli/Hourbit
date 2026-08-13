using System.Windows.Input;

namespace Hourbit.App.Commands;

public interface IAsyncCommand : ICommand
{
    bool IsRunning { get; }
    Task ExecuteAsync(object? parameter);
    void RaiseCanExecuteChanged();
}

public sealed class AsyncCommand(
    Func<object?, CancellationToken, Task> execute,
    Func<object?, bool>? canExecute = null) : ObservableObject, IAsyncCommand
{
    private readonly Func<object?, CancellationToken, Task> _execute =
        execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<object?, bool>? _canExecute = canExecute;
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        OnPropertyChanged(nameof(IsRunning));
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter, CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            OnPropertyChanged(nameof(IsRunning));
            RaiseCanExecuteChanged();
        }
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
