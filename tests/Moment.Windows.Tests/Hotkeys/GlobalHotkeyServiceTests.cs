using Moment.Windows.Hotkeys;

namespace Moment.Windows.Tests.Hotkeys;

public sealed class GlobalHotkeyServiceTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", 0x0003u, 0x20u)]
    [InlineData("Ctrl+Shift+R", 0x0006u, 0x52u)]
    [InlineData("Win+F12", 0x0008u, 0x7Bu)]
    public void Maps_supported_gestures(string text, uint modifiers, uint key)
    {
        Assert.Equal((modifiers, key), HotkeyGestureParser.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("R")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("Ctrl+R+S")]
    [InlineData("Ctrl+Ctrl+R")]
    [InlineData(" Ctrl+R")]
    [InlineData("Ctrl++R")]
    public void Rejects_non_canonical_or_unsupported_gestures(string text)
    {
        Assert.Throws<FormatException>(() => HotkeyGestureParser.Parse(text));
    }

    [Fact]
    public void Registration_translates_native_failure_to_conflict_and_still_unregisters()
    {
        var window = new HotkeyWindow { RegistrationSucceeds = false };
        var service = new GlobalHotkeyService(window);

        Assert.Equal(HotkeyRegistrationResult.Conflict, service.Register("Ctrl+Shift+R"));
        service.Dispose();
        service.Dispose();

        Assert.Equal(1, window.RegisterCalls);
        Assert.Equal(1, window.UnregisterCalls);
    }

    [Fact]
    public void Native_hotkey_is_forwarded_and_successful_registration_is_unregistered()
    {
        var window = new HotkeyWindow { RegistrationSucceeds = true };
        var service = new GlobalHotkeyService(window);
        var presses = 0;
        service.Pressed += (_, _) => presses++;

        Assert.Equal(HotkeyRegistrationResult.Registered, service.Register("Ctrl+Alt+Space"));
        window.Raise();
        service.Dispose();

        Assert.Equal(1, presses);
        Assert.Equal((0x0003u, 0x20u), window.LastGesture);
        Assert.Equal(1, window.UnregisterCalls);
    }

    [Fact]
    public void Repeated_registration_replaces_the_previous_native_registration()
    {
        var window = new HotkeyWindow { RegistrationSucceeds = true };
        using var service = new GlobalHotkeyService(window);

        Assert.Equal(HotkeyRegistrationResult.Registered, service.Register("Ctrl+Alt+Space"));
        Assert.Equal(HotkeyRegistrationResult.Registered, service.Register("Ctrl+Shift+R"));

        Assert.Equal(2, window.RegisterCalls);
        Assert.Equal(1, window.UnregisterCalls);
    }

    private sealed class HotkeyWindow : IHotkeyWindow
    {
        public event EventHandler? HotkeyPressed;
        public bool RegistrationSucceeds { get; set; }
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public (uint, uint) LastGesture { get; private set; }

        public bool Register(uint modifiers, uint virtualKey)
        {
            RegisterCalls++;
            LastGesture = (modifiers, virtualKey);
            return RegistrationSucceeds;
        }

        public void Unregister() => UnregisterCalls++;
        public void Raise() => HotkeyPressed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
}
