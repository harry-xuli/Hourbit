using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Moment.Windows.Alerts;

namespace Moment.App.Settings;

public sealed record SettingsViewActions(
    IImportantAlertAudio PreviewAudio,
    Func<CancellationToken, Task> SendTestNotification,
    Func<CancellationToken, Task> ShowTestImportantAlert,
    string DataFolder,
    Action<string>? OpenFolder = null,
    Func<string?>? SelectBackupExportPath = null,
    Func<string?>? SelectBackupRestorePath = null,
    Func<string, bool>? ConfirmRestore = null);

public partial class SettingsView : Window
{
    private readonly SettingsViewActions? _actions;

    public SettingsView()
    {
        InitializeComponent();
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
        await RunSaveAsync(
            ct => ViewModel.SaveHotkeyAsync(HotkeyBox.Text, ct),
            "快捷键已保存");
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        await RunSaveAsync(ct => ViewModel.SaveAsync(ct), "设置已保存");
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

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        await RunAsync(
            async ct => _ = await ViewModel.CreateBackupAsync(ct),
            "备份已创建");
    }

    private async void OnExportBackup(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        var path = _actions?.SelectBackupExportPath?.Invoke()
                   ?? SelectBackupExportPath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        await RunAsync(
            ct => ViewModel.ExportBackupAsync(path, ct),
            "备份已导出");
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        var path = _actions?.SelectBackupRestorePath?.Invoke()
                   ?? SelectBackupRestorePath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        var confirmed = _actions?.ConfirmRestore?.Invoke(path)
                        ?? ConfirmRestore(path);
        if (!confirmed)
            return;
        await RunAsync(
            ct => ViewModel.RestoreBackupAsync(path, ct),
            "备份已恢复");
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        try
        {
            ViewModel.OpenReleasePage();
            ActionStatusText.Text = "已在浏览器中打开发布页面";
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = exception.Message;
        }
    }

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

    private async Task RunSaveAsync(
        Func<CancellationToken, Task<SettingsSaveResult>> action,
        string successMessage)
    {
        try
        {
            var result = await action(CancellationToken.None);
            ActionStatusText.Text = result.Succeeded
                ? successMessage
                : result.ErrorMessage;
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
            if (_actions?.OpenFolder is { } openFolder)
            {
                openFolder(path);
                return;
            }

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

    private string? SelectBackupExportPath()
    {
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出备份",
            Filter = "Hourbit 日程备份 (*.moment-backup)|*.moment-backup",
            AddExtension = true,
            DefaultExt = ".moment-backup",
            FileName = $"moment-export-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}.moment-backup"
        };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private string? SelectBackupRestorePath()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "从备份恢复",
            Filter = "Hourbit 日程备份 (*.moment-backup)|*.moment-backup",
            CheckFileExists = true,
            Multiselect = false
        };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private bool ConfirmRestore(string path) =>
        System.Windows.MessageBox.Show(
            this,
            $"恢复将用所选备份替换当前提醒数据。\n\n{path}\n\n是否继续？",
            "确认恢复备份",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;
}
