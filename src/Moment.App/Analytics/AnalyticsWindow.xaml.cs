namespace Moment.App.Analytics;

public partial class AnalyticsWindow : System.Windows.Window
{
    public AnalyticsWindow()
    {
        InitializeComponent();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
    }
}
