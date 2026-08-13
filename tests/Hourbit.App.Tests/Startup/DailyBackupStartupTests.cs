using Hourbit.App.Settings;
using Hourbit.Core.Abstractions;
using Hourbit.Infrastructure.Backup;
using Hourbit.Windows.Hotkeys;
using System.IO;

namespace Hourbit.App.Tests.Startup;

public sealed class DailyBackupStartupTests
{
    [Fact]
    public async Task Daily_backup_failure_sets_persistent_warning_without_blocking_startup()
    {
        var settings = new SettingsViewModel(
            new Hotkeys(),
            new Store());
        var remindersStarted = false;

        await CompositionRoot.TryCreateDailyBackupAsync(
            new FailingBackupService(), settings, default);
        remindersStarted = true;

        Assert.True(remindersStarted);
        Assert.Equal(
            "自动备份失败：disk full",
            settings.WarningMessage);
    }

    private sealed class FailingBackupService : IBackupService
    {
        public Task<string> CreateDailyBackupAsync(CancellationToken ct) =>
            Task.FromException<string>(new IOException("disk full"));
        public Task ExportAsync(string destinationPath, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RestoreAsync(string backupPath, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class Hotkeys : IGlobalHotkeyService
    {
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }
        public HotkeyRegistrationResult Register(string gesture) =>
            HotkeyRegistrationResult.Registered;
        public void Dispose() { }
    }

    private sealed class Store : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new AppSettings(
                "Ctrl+Alt+Space", false, 100, null));
        public Task SaveAsync(AppSettings settings, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
