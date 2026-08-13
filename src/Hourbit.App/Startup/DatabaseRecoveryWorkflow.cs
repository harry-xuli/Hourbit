using Hourbit.Infrastructure.Backup;

namespace Hourbit.App.Startup;

internal interface IPreCompositionRecoveryDialog
{
    string? SelectBackup(string corruptCopyPath);
    bool ConfirmRestore(string backupPath, string corruptCopyPath);
}

internal static class DatabaseRecoveryWorkflow
{
    internal static async Task<bool> RunAsync(
        DatabaseRecoveryService recovery,
        DatabaseRecoveryResult result,
        IPreCompositionRecoveryDialog dialog,
        CancellationToken ct)
    {
        if (result.Status != DatabaseRecoveryStatus.RequiresUserDecision)
            return true;
        var corruptCopyPath = result.CorruptDatabasePath
            ?? throw new InvalidOperationException(
                "Corrupt database copy path is unavailable.");
        var backupPath = dialog.SelectBackup(corruptCopyPath);
        if (string.IsNullOrWhiteSpace(backupPath))
            return false;
        if (!dialog.ConfirmRestore(backupPath, corruptCopyPath))
            return false;
        await recovery.RestoreUserSelectedAsync(backupPath, ct);
        return true;
    }
}

internal sealed class WpfPreCompositionRecoveryDialog :
    IPreCompositionRecoveryDialog
{
    public string? SelectBackup(string corruptCopyPath)
    {
        var choose = System.Windows.MessageBox.Show(
            "提醒数据库已损坏，且没有可自动恢复的有效备份。\n\n"
            + $"损坏数据已原样保存在：\n{corruptCopyPath}\n\n"
            + "选择“是”可选择一个 .moment-backup 文件恢复；"
            + "选择“否”将退出且不会替换当前数据库。",
            "Hourbit 日程 - 数据恢复",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (choose != System.Windows.MessageBoxResult.Yes)
            return null;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Hourbit 日程备份",
            Filter = "Hourbit 日程备份 (*.moment-backup)|*.moment-backup",
            CheckFileExists = true,
            Multiselect = false
        };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    public bool ConfirmRestore(
        string backupPath,
        string corruptCopyPath) =>
        System.Windows.MessageBox.Show(
            "所选备份将先经过格式、校验和、架构和 SQLite 完整性验证，"
            + "验证通过后才替换损坏数据库。\n\n"
            + $"所选备份：\n{backupPath}\n\n"
            + $"保留的损坏副本：\n{corruptCopyPath}\n\n"
            + "是否确认恢复？",
            "确认恢复数据",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;
}
