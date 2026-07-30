using Moment.App.Commands;
using Moment.Core.Abstractions;
using Moment.Windows.Hotkeys;
using Moment.Windows.Startup;
using System.IO;

namespace Moment.App.Settings;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly ISettingsStore _store;
    private readonly IStartupRegistrationService? _startup;
    private readonly string? _executablePath;
    private string _hotkey = "Ctrl+Alt+Space";
    private bool _startWithWindows;
    private int _alertVolume = 100;
    private string? _customAlertSoundPath;
    private string? _hotkeyError;
    private string? _warningMessage;
    private bool _missingSoundWarningShown;

    public SettingsViewModel(
        IGlobalHotkeyService hotkeys,
        ISettingsStore store)
        : this(hotkeys, store, null, null)
    {
    }

    public SettingsViewModel(
        IGlobalHotkeyService hotkeys,
        ISettingsStore store,
        IStartupRegistrationService? startup,
        string? executablePath)
    {
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _startup = startup;
        _executablePath = executablePath;
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

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await _store.LoadAsync(ct);
        Hotkey = settings.Hotkey;
        StartWithWindows = settings.StartWithWindows;
        AlertVolume = settings.AlertVolume;
        CustomAlertSoundPath = settings.CustomAlertSoundPath;

        if (ResetInvalidCustomSound())
            await _store.SaveAsync(CurrentSettings(), ct);

        ApplyStartupSetting();
    }

    public async Task SaveHotkeyAsync(string hotkey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_hotkeys.Register(hotkey) == HotkeyRegistrationResult.Conflict)
        {
            HotkeyError = "该快捷键已被其他程序占用";
            return;
        }

        Hotkey = hotkey;
        HotkeyError = null;
        await SaveAsync(ct);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        ResetInvalidCustomSound();
        await _store.SaveAsync(CurrentSettings(), ct);
        ApplyStartupSetting();
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
            return false;

        CustomAlertSoundPath = null;
        if (!_missingSoundWarningShown)
        {
            _missingSoundWarningShown = true;
            WarningMessage = !exists
                ? "自定义声音文件不存在，已恢复为内置声音"
                : "请选择有效的 WAV 声音文件，已恢复为内置声音";
        }
        return true;
    }

    private AppSettings CurrentSettings() =>
        new(Hotkey, StartWithWindows, AlertVolume, CustomAlertSoundPath);

    private void ApplyStartupSetting()
    {
        if (_startup is not null && !string.IsNullOrWhiteSpace(_executablePath))
            _startup.SetEnabled(StartWithWindows, _executablePath);
    }
}
