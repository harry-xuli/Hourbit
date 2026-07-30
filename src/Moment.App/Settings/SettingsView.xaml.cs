using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Moment.Windows.Alerts;

namespace Moment.App.Settings;

public sealed record SettingsViewActions(
    IImportantAlertAudio PreviewAudio,
    Func<CancellationToken, Task> SendTestNotification,
    Func<CancellationToken, Task> ShowTestImportantAlert,
    string DataFolder);

public partial class SettingsView : Window
{
    private readonly SettingsViewActions? _actions;

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text =
            $"当前版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知"}";
    }

    public SettingsView(
        SettingsViewModel viewModel,
        SettingsViewActions actions) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        await RunAsync(ct => ViewModel.LoadAsync(ct), "设置已加载");
    }

    private async void OnSaveHotkey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        await RunAsync(
            ct => ViewModel.SaveHotkeyAsync(HotkeyBox.Text, ct),
            "快捷键已保存");
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        await RunAsync(ct => ViewModel.SaveAsync(ct), "设置已保存");
    }

    private void OnPickSound(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择提醒声音",
            Filter = "WAV 声音文件 (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            ViewModel.CustomAlertSoundPath = picker.FileName;
    }

    private async void OnPreviewSound(object sender, RoutedEventArgs e)
    {
        if (_actions is null || ViewModel is null)
            return;
        await RunAsync(async ct =>
        {
            await _actions.PreviewAudio.StopAsync(ct);
            if (string.IsNullOrWhiteSpace(ViewModel.CustomAlertSoundPath))
                await _actions.PreviewAudio.StartDefaultLoopAsync(ct);
            else
                await _actions.PreviewAudio.StartCustomLoopAsync(
                    ViewModel.CustomAlertSoundPath, ct);
        }, "正在循环播放声音预览");
    }

    private async void OnStopSound(object sender, RoutedEventArgs e)
    {
        if (_actions is null)
            return;
        await RunAsync(
            ct => _actions.PreviewAudio.StopAsync(ct),
            "声音预览已停止");
    }

    private async void OnTestNormalNotification(object sender, RoutedEventArgs e)
    {
        if (_actions is null)
            return;
        await RunAsync(_actions.SendTestNotification, "测试通知已发送");
    }

    private async void OnTestImportantAlert(object sender, RoutedEventArgs e)
    {
        if (_actions is null)
            return;
        await RunAsync(_actions.ShowTestImportantAlert, "重要提醒测试已结束");
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(_actions?.DataFolder);

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(_actions is null
            ? null
            : Path.Combine(_actions.DataFolder, "backups"));

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_actions is null)
            return;
        try
        {
            await _actions.PreviewAudio.StopAsync(CancellationToken.None);
            if (_actions.PreviewAudio is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
        catch
        {
            // The window is already closing; preview cleanup cannot be shown here.
        }
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        string successMessage)
    {
        try
        {
            await action(CancellationToken.None);
            ActionStatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = exception.Message;
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = exception.Message;
        }
    }
}
