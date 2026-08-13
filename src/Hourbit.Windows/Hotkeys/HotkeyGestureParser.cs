namespace Hourbit.Windows.Hotkeys;

public static class HotkeyGestureParser
{
    private const uint Alt = 0x0001;
    private const uint Control = 0x0002;
    private const uint Shift = 0x0004;
    private const uint Windows = 0x0008;

    public static (uint Modifiers, uint VirtualKey) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
            throw Invalid(text);

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Length < 2 || parts.Any(string.IsNullOrEmpty))
            throw Invalid(text);

        uint modifiers = 0;
        uint key = 0;
        foreach (var part in parts)
        {
            uint modifier = part switch
            {
                "Alt" => Alt,
                "Ctrl" => Control,
                "Shift" => Shift,
                "Win" => Windows,
                _ => 0u
            };

            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0 || key != 0)
                    throw Invalid(text);
                modifiers |= modifier;
                continue;
            }

            if (key != 0 || !TryParseKey(part, out key))
                throw Invalid(text);
        }

        if (modifiers == 0 || key == 0)
            throw Invalid(text);

        return (modifiers, key);
    }

    private static bool TryParseKey(string text, out uint key)
    {
        key = 0;
        if (text == "Space")
        {
            key = 0x20;
            return true;
        }

        if (text.Length == 1 && text[0] is >= 'A' and <= 'Z')
        {
            key = text[0];
            return true;
        }

        if (text.Length == 1 && text[0] is >= '0' and <= '9')
        {
            key = text[0];
            return true;
        }

        if (text.Length >= 2 && text[0] == 'F' &&
            int.TryParse(text.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = (uint)(0x70 + functionKey - 1);
            return true;
        }

        return false;
    }

    private static FormatException Invalid(string? text) =>
        new($"'{text}' is not a supported canonical hotkey gesture.");
}
