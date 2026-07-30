namespace Moment.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
