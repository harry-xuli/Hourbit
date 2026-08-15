using Hourbit.App.Commands;
using Hourbit.App.Alerts;
using Hourbit.Core.Abstractions;
using Hourbit.Windows.Hotkeys;
using Hourbit.Windows.Startup;
using Hourbit.Infrastructure.Backup;
using System.IO;

namespace Hourbit.App.Settings;

public sealed record SettingsSaveResult(bool Succeeded, string? ErrorMessage)
{
    public static SettingsSaveResult Success { get; } = new(true, null);
    public static SettingsSaveResult Failure(string message) => new(false, message);
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly ISettingsStore _store;
    private readonly IStartupRegistrationService? _startup;
    private readonly string? _executablePath;
    private readonly IBackupService? _backupService;
    private readonly IReleasePageService? _releasePage;
    private readonly IDataResetCoordinator? _resetCoordinator;
    private string _resetConfirmationText = string.Empty;
    private string _hotkey = "Ctrl+Alt+Space";
    private bool _startWithWindows;
    private int _alertVolume = 100;
    private string? _customAlertSoundPath;
    private string? _uiLanguage;
    private string? _hotkeyError;
    private string? _warningMessage;
    private bool _missingSoundWarningShown;
    private AppSettings _persisted =
        new("Ctrl+Alt+Space", false, 100, null);

    public SettingsViewModel(
        IGlobalHotkeyService hotkeys,
        ISettingsStore store)
        : this(hotkeys, store, null, null, null, null)
    {
    }

    public SettingsViewModel(
        IGlobalHotkeyService hotkeys,
        ISettingsStore store,
        IStartupRegistrationService? startup = null,
        string? executablePath = null,
        IBackupService? backupService = null,
        IReleasePageService? releasePage = null,
        IDataResetCoordinator? resetCoordinator = null)
    {
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _startup = startup;
        _executablePath = executablePath;
        _backupService = backupService;
        _releasePage = releasePage;
        _resetCoordinator = resetCoordinator;
    }

    public string Hotkey
    {
        get => _hotkey;
        private set => SetProperty(ref _hotkey, value);
    }

    public string? HotkeyError
    {
        get => _hotkeyError;
        private set => SetProperty(ref _hotkeyError, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public int AlertVolume
    {
        get => _alertVolume;
        set => SetProperty(ref _alertVolume, Math.Clamp(value, 0, 100));
    }

    public string? CustomAlertSoundPath
    {
        get => _customAlertSoundPath;
        set => SetProperty(ref _customAlertSoundPath,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string? WarningMessage
    {
        get => _warningMessage;
        private set => SetProperty(ref _warningMessage, value);
    }

    public string? UiLanguage
    {
        get => _uiLanguage;
        private set => SetProperty(ref _uiLanguage, value);
    }

    public bool HasReleasePage => _releasePage?.Url is not null;
    public string VersionText =>
        (_releasePage?.Metadata ??
         ProductMetadata.FromAssembly(typeof(SettingsViewModel).Assembly))
        .SettingsFooterText;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await _store.LoadAsync(ct);
        Hotkey = settings.Hotkey;
        StartWithWindows = settings.StartWithWindows;
        AlertVolume = settings.AlertVolume;
        CustomAlertSoundPath = settings.CustomAlertSoundPath;
        UiLanguage = settings.UiLanguage;
        _persisted = settings;

        if (ResetInvalidCustomSound())
        {
            await _store.SaveAsync(CurrentSettings(), ct);
            _persisted = CurrentSettings();
        }

        ApplyStartupSetting();
    }

    public async Task<SettingsSaveResult> SaveHotkeyAsync(
        string hotkey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_hotkeys.Register(hotkey) == HotkeyRegistrationResult.Conflict)
        {
            HotkeyError = "该快捷键已被其他程序占用";
            return SettingsSaveResult.Failure(HotkeyError);
        }

        var previousHotkey = Hotkey;
        var desired = _persisted with { Hotkey = hotkey };
        try
        {
            await _store.SaveAsync(desired, ct);
        }
        catch (Exception exception)
        {
            _hotkeys.Register(previousHotkey);
            HotkeyError = exception.Message;
            return SettingsSaveResult.Failure(exception.Message);
        }

        Hotkey = hotkey;
        _persisted = desired;
        HotkeyError = null;
        return SettingsSaveResult.Success;
    }

    public async Task<SettingsSaveResult> SaveUiLanguageAsync(
        string uiLanguage,
        CancellationToken ct = default)
    {
        if (uiLanguage is not ("zh-CN" or "en-US"))
            return SettingsSaveResult.Failure("不支持的界面语言。");

        var desired = _persisted with { UiLanguage = uiLanguage };
        try
        {
            await _store.SaveAsync(desired, ct);
        }
        catch (Exception exception)
        {
            return SettingsSaveResult.Failure(exception.Message);
        }

        UiLanguage = uiLanguage;
        _persisted = desired;
        return SettingsSaveResult.Success;
    }

    public async Task<SettingsSaveResult> SaveAsync(
        CancellationToken ct = default)
    {
        ResetInvalidCustomSound();
        var desired = CurrentSettings();
        var startupChanged = desired.StartWithWindows != _persisted.StartWithWindows;
        try
        {
            if (startupChanged)
                ApplyStartupSetting();
        }
        catch (Exception exception)
        {
            StartWithWindows = _persisted.StartWithWindows;
            return SettingsSaveResult.Failure(exception.Message);
        }

        try
        {
            await _store.SaveAsync(desired, ct);
        }
        catch (Exception exception)
        {
            if (startupChanged)
            {
                StartWithWindows = _persisted.StartWithWindows;
                ApplyStartupSetting();
            }
            return SettingsSaveResult.Failure(exception.Message);
        }

        _persisted = desired;
        return SettingsSaveResult.Success;
    }

    public Task<string> CreateBackupAsync(CancellationToken ct = default) =>
        GetBackupService().CreateDailyBackupAsync(ct);

    public Task ExportBackupAsync(
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return GetBackupService().ExportAsync(destinationPath, ct);
    }

    public Task RestoreBackupAsync(
        string backupPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        return GetBackupService().RestoreAsync(backupPath, ct);
    }

    public string ResetConfirmationText
    {
        get => _resetConfirmationText;
        set
        {
            if (SetProperty(ref _resetConfirmationText, value ?? string.Empty))
                OnPropertyChanged(nameof(CanResetLocalData));
        }
    }

    public bool CanResetLocalData =>
        _resetCoordinator is not null && ResetConfirmationText == "重置 Hourbit";

    public async Task RequestResetAsync(
        string backupPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var coordinator = _resetCoordinator ??
            throw new InvalidOperationException("数据重置未配置。");
        if (!CanResetLocalData)
            throw new InvalidOperationException("请输入确认短语「重置 Hourbit」。");

        _ = await coordinator.RequestAsync(backupPath, ct);
        WarningMessage = "备份已完成，Hourbit 将重新启动为空白状态。";
    }

    public void OpenReleasePage() =>
        (_releasePage ??
         throw new InvalidOperationException("Release page is not configured."))
        .Open();

    internal void ReportAutomaticBackupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WarningMessage = $"自动备份失败：{exception.Message}";
    }

    private bool ResetInvalidCustomSound()
    {
        if (CustomAlertSoundPath is null)
            return false;

        var hasWaveExtension = string.Equals(
            Path.GetExtension(CustomAlertSoundPath),
            ".wav",
            StringComparison.OrdinalIgnoreCase);
        var exists = File.Exists(CustomAlertSoundPath);
        if (hasWaveExtension && exists)
        {
            try
            {
                SupportedPcmWave.ValidateFile(CustomAlertSoundPath);
                return false;
            }
            catch (InvalidDataException)
            {
            }
        }

        CustomAlertSoundPath = null;
        if (!_missingSoundWarningShown)
        {
            _missingSoundWarningShown = true;
            WarningMessage = !exists
                ? "自定义声音文件不存在，已恢复为内置声音"
                : "请选择未压缩 PCM 8 位或 16 位 WAV 声音文件，已恢复为内置声音";
        }
        return true;
    }

    private AppSettings CurrentSettings() =>
        new(Hotkey, StartWithWindows, AlertVolume, CustomAlertSoundPath, UiLanguage);

    private IBackupService GetBackupService() =>
        _backupService ??
        throw new InvalidOperationException("Backup service is not configured.");

    private void ApplyStartupSetting()
    {
        if (_startup is not null && !string.IsNullOrWhiteSpace(_executablePath))
            _startup.SetEnabled(StartWithWindows, _executablePath);
    }
}
