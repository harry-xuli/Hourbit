using System.IO;
using Hourbit.Core.Reporting;

namespace Hourbit.App.Analytics;

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

    private async void OnExportReport(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not AnalyticsViewModel viewModel)
            return;

        var privacy = ChoosePrivacy();
        if (privacy is null)
            return;

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出报告",
            Filter = "PDF 报告 (*.pdf)|*.pdf",
            AddExtension = true,
            DefaultExt = ".pdf",
            FileName = $"hourbit-report-{DateTimeOffset.UtcNow:yyyyMMdd}"
        };
        if (picker.ShowDialog(this) != true)
            return;

        var basePath = StripPdfExtension(picker.FileName);
        try
        {
            var paths = await viewModel.ExportReportAsync(
                privacy.Value, basePath, CancellationToken.None);
            System.Windows.MessageBox.Show(
                this,
                "报告已导出：\n" + string.Join("\n", paths),
                "导出报告",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this, exception.Message, "导出失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private ReportPrivacyMode? ChoosePrivacy()
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "完整报告包含标题与记录标识。选择「否」将导出匿名统计报告。",
            "选择隐私模式",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Yes);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => ReportPrivacyMode.Full,
            System.Windows.MessageBoxResult.No => ReportPrivacyMode.Anonymous,
            _ => null
        };
    }

    private static string StripPdfExtension(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
}
