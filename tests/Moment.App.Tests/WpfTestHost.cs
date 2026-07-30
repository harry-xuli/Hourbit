namespace Moment.App.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Task<System.Windows.Threading.Dispatcher>> Dispatcher =
        new(StartDispatcher);

    public static async Task RunAsync(Action action)
    {
        var dispatcher = await Dispatcher.Value;
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetResult(exception);
            }
            finally
            {
                foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                    window.Close();
            }
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
