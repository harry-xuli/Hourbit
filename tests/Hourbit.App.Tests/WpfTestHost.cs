namespace Hourbit.App.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Task<System.Windows.Threading.Dispatcher>> Dispatcher =
        new(StartDispatcher);

    public static Task RunAsync(Action action) =>
        RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });

    public static async Task RunAsync(Func<Task> action)
    {
        var dispatcher = await Dispatcher.Value;
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            Exception? failure = null;
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                var windows = System.Windows.Application.Current.Windows
                    .Cast<System.Windows.Window>()
                    .ToArray();
                foreach (var window in windows)
                    window.Close();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            completion.TrySetResult(failure);
        }));

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(exception);
    }

    private static Task<System.Windows.Threading.Dispatcher> StartDispatcher()
    {
        var started = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();
                var application = new App();
                application.InitializeComponent();
                started.TrySetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task;
    }
}
