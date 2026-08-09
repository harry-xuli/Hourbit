namespace Moment.App.Analytics;

public partial class AnalyticsWindow : System.Windows.Window
{
    public AnalyticsWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is AnalyticsViewModel viewModel)
                viewModel.CancelActiveLoad();
        };
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
    }
}
