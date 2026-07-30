using Moment.App.Settings;
using Moment.Core.Abstractions;
using Moment.TestSupport;
using Moment.Windows.Hotkeys;
using Moment.Windows.Startup;
using System.IO;

namespace Moment.App.Tests.Settings;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Conflicting_hotkey_is_not_saved_and_exposes_help_text()
    {
        var hotkeys = new StubHotkeys(HotkeyRegistrationResult.Conflict);
        var store = new RecordingSettingsStore();
        var vm = new SettingsViewModel(hotkeys, store);

        await vm.SaveHotkeyAsync("Ctrl+Alt+Space");

        Assert.Equal("该快捷键已被其他程序占用", vm.HotkeyError);
        Assert.Null(store.LastSavedHotkey);
    }

    [Fact]
    public async Task Saving_a_registered_hotkey_preserves_the_other_loaded_settings()
    {
        using var directory = new TempDirectory();
        var soundPath = Path.Combine(directory.Path, "bell.wav");
        await File.WriteAllBytesAsync(soundPath, CreatePcmWave());
        var hotkeys = new StubHotkeys(HotkeyRegistrationResult.Registered);
        var store = new RecordingSettingsStore
        {
            Current = new AppSettings("Ctrl+Alt+Space", true, 42, soundPath)
        };
        var vm = new SettingsViewModel(hotkeys, store);
        await vm.LoadAsync();

        await vm.SaveHotkeyAsync("Ctrl+Shift+R");

        Assert.Equal(
            new AppSettings("Ctrl+Shift+R", true, 42, soundPath),
            store.LastSaved);
        Assert.Null(vm.HotkeyError);
    }

    [Fact]
    public async Task Hotkey_store_failure_restores_the_previous_runtime_registration()
    {
        var hotkeys = new StubHotkeys(HotkeyRegistrationResult.Registered);
        var store = new RecordingSettingsStore
        {
            Current = new AppSettings("Ctrl+Alt+Space", false, 100, null),
            SaveException = new IOException("settings write failed")
        };
        var vm = new SettingsViewModel(hotkeys, store);
        await vm.LoadAsync();

        var result = await vm.SaveHotkeyAsync("Ctrl+Shift+R");

        Assert.False(result.Succeeded);
        Assert.Equal(["Ctrl+Shift+R", "Ctrl+Alt+Space"], hotkeys.Gestures);
        Assert.Equal("Ctrl+Alt+Space", vm.Hotkey);
        Assert.Equal("settings write failed", vm.HotkeyError);
        Assert.Equal("Ctrl+Alt+Space", store.Current.Hotkey);
    }

    [Fact]
    public async Task Hotkey_save_commits_only_hotkey_and_full_save_later_validates_dirty_draft()
    {
        using var directory = new TempDirectory();
        var malformedWave = Path.Combine(directory.Path, "malformed.wav");
        await File.WriteAllBytesAsync(malformedWave, [1, 2, 3, 4]);
        var startup = new StubStartup();
        var store = new RecordingSettingsStore
        {
            Current = new AppSettings(
                "Ctrl+Alt+Space", false, 100, null)
        };
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered),
            store,
            startup,
            @"C:\Program Files\Moment\Moment.exe");
        await vm.LoadAsync();
        startup.Calls.Clear();
        vm.StartWithWindows = true;
        vm.AlertVolume = 33;
        vm.CustomAlertSoundPath = malformedWave;

        var hotkeyResult = await vm.SaveHotkeyAsync("Ctrl+Shift+R");

        Assert.True(hotkeyResult.Succeeded);
        Assert.Equal(
            new AppSettings("Ctrl+Shift+R", false, 100, null),
            store.LastSaved);
        Assert.Empty(startup.Calls);
        Assert.True(vm.StartWithWindows);
        Assert.Equal(33, vm.AlertVolume);
        Assert.Equal(malformedWave, vm.CustomAlertSoundPath);

        var fullResult = await vm.SaveAsync();

        Assert.True(fullResult.Succeeded);
        Assert.Equal(
            new AppSettings("Ctrl+Shift+R", true, 33, null),
            store.LastSaved);
        Assert.Equal([true],
            startup.Calls.Select(call => call.Enabled).ToArray());
        Assert.Null(vm.CustomAlertSoundPath);
        Assert.Equal(
            "请选择未压缩 PCM 8 位或 16 位 WAV 声音文件，已恢复为内置声音",
            vm.WarningMessage);
    }

    [Fact]
    public async Task Missing_custom_wave_resets_to_embedded_sound_and_warns_non_modally_once()
    {
        var store = new RecordingSettingsStore
        {
            Current = new AppSettings("Ctrl+Alt+Space", false, 75, @"C:\missing\alert.wav")
        };
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered), store);
        var warnings = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.WarningMessage))
                warnings++;
        };

        await vm.LoadAsync();
        await vm.SaveAsync();

        Assert.Null(vm.CustomAlertSoundPath);
        Assert.Equal("自定义声音文件不存在，已恢复为内置声音", vm.WarningMessage);
        Assert.Equal(1, warnings);
        Assert.All(store.Saves, saved => Assert.Null(saved.CustomAlertSoundPath));
    }

    [Fact]
    public void Alert_volume_is_limited_to_the_supported_zero_to_one_hundred_range()
    {
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered),
            new RecordingSettingsStore());

        vm.AlertVolume = -1;
        Assert.Equal(0, vm.AlertVolume);

        vm.AlertVolume = 101;
        Assert.Equal(100, vm.AlertVolume);
    }

    [Fact]
    public async Task Existing_non_wave_path_is_not_persisted_as_an_alert_sound()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "alert.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var store = new RecordingSettingsStore();
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered), store)
        {
            CustomAlertSoundPath = path
        };

        await vm.SaveAsync();

        Assert.Null(vm.CustomAlertSoundPath);
        Assert.Null(store.LastSaved!.CustomAlertSoundPath);
        Assert.Equal("请选择未压缩 PCM 8 位或 16 位 WAV 声音文件，已恢复为内置声音",
            vm.WarningMessage);
    }

    [Fact]
    public async Task Saving_startup_toggle_reuses_the_windows_startup_service()
    {
        var startup = new StubStartup();
        var store = new RecordingSettingsStore();
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered),
            store,
            startup,
            @"C:\Program Files\Moment\Moment.exe")
        {
            StartWithWindows = true
        };

        await vm.SaveAsync();

        Assert.Equal(
            (true, @"C:\Program Files\Moment\Moment.exe"),
            startup.LastSet);
        Assert.True(store.LastSaved!.StartWithWindows);
    }

    [Fact]
    public async Task Startup_registry_failure_keeps_persisted_setting_unchanged()
    {
        var startup = new StubStartup();
        var store = new RecordingSettingsStore();
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered),
            store,
            startup,
            @"C:\Program Files\Moment\Moment.exe");
        await vm.LoadAsync();
        startup.SetException =
            new UnauthorizedAccessException("registry denied");
        vm.StartWithWindows = true;

        var result = await vm.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("registry denied", result.ErrorMessage);
        Assert.False(vm.StartWithWindows);
        Assert.Empty(store.Saves);
        Assert.False(store.Current.StartWithWindows);
    }

    [Fact]
    public async Task Startup_store_failure_compensates_the_registry_change()
    {
        var startup = new StubStartup();
        var store = new RecordingSettingsStore
        {
            SaveException = new IOException("settings write failed")
        };
        var vm = new SettingsViewModel(
            new StubHotkeys(HotkeyRegistrationResult.Registered),
            store,
            startup,
            @"C:\Program Files\Moment\Moment.exe");
        await vm.LoadAsync();
        startup.Calls.Clear();
        vm.StartWithWindows = true;

        var result = await vm.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal([true, false],
            startup.Calls.Select(call => call.Enabled).ToArray());
        Assert.False(vm.StartWithWindows);
        Assert.False(store.Current.StartWithWindows);
    }

    private sealed class StubHotkeys(HotkeyRegistrationResult result) : IGlobalHotkeyService
    {
        public List<string> Gestures { get; } = [];
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }
        public HotkeyRegistrationResult Register(string gesture)
        {
            Gestures.Add(gesture);
            return result;
        }
        public void Dispose() { }
    }

    private static byte[] CreatePcmWave()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream,
                   System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(38);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(8000);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(1);
            writer.Write((byte)128);
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public AppSettings Current { get; set; } =
            new("Ctrl+Alt+Space", false, 100, null);
        public Exception? SaveException { get; set; }
        public List<AppSettings> Saves { get; } = [];
        public AppSettings? LastSaved => Saves.LastOrDefault();
        public string? LastSavedHotkey => LastSaved?.Hotkey;

        public Task<AppSettings> LoadAsync(CancellationToken ct) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            if (SaveException is not null)
                return Task.FromException(SaveException);
            Saves.Add(settings);
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StubStartup : IStartupRegistrationService
    {
        public bool IsEnabled => LastSet?.Enabled ?? false;
        public (bool Enabled, string Path)? LastSet { get; private set; }
        public Exception? SetException { get; set; }
        public List<(bool Enabled, string Path)> Calls { get; } = [];
        public StartupPathStatus GetPathStatus(string executablePath) =>
            IsEnabled ? StartupPathStatus.Current : StartupPathStatus.Disabled;
        public void SetEnabled(bool enabled, string executablePath)
        {
            if (SetException is not null)
                throw SetException;
            LastSet = (enabled, executablePath);
            Calls.Add(LastSet.Value);
        }
    }
}
