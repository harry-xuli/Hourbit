using System.ComponentModel;

namespace Moment.App;

public partial class MainWindow : System.Windows.Window
{
    private bool _allowExit;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void AllowExit() => _allowExit = true;

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowExit)
            return;
        eventArgs.Cancel = true;
        Hide();
    }
}
