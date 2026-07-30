namespace Moment.App.QuickAdd;

public interface IQuickAddWindow
{
    bool IsClosed { get; }
    void ShowAndFocus();
}

public sealed class QuickAddWindowController(
    Func<IQuickAddWindow> createWindow,
    System.Windows.Threading.Dispatcher? dispatcher = null)
{
    private IQuickAddWindow? _window;

    public void ShowAndFocus()
    {
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ShowAndFocus));
            return;
        }

        if (_window is null || _window.IsClosed)
            _window = createWindow();
        _window.ShowAndFocus();
    }
}
