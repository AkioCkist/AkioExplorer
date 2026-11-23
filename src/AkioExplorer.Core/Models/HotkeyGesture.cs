namespace AkioExplorer.Core.Models;

/// <summary>Modifier keys of a global hotkey (same bits as Win32 RegisterHotKey's MOD_*).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A key combination such as "Alt+Space" or "Ctrl+Shift+N": modifiers plus one virtual-key code. Used for the global
/// quick-search hotkey (<see cref="IsValid"/>) and for in-app shortcuts (<see cref="IsValidShortcut"/>).
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Alt, 0x20); // Alt+Space

    /// <summary>A hotkey needs at least one modifier (a bare key would be stolen from every app).</summary>
    public bool IsValid => Modifiers != HotkeyModifiers.None && Key != 0 && !IsModifierKey(Key);

    /// <summary>
    /// An in-app shortcut: function keys may stand alone (F5) or with Shift; anything else needs Ctrl, Alt or Win,
    /// so typing in the address bar, the filter box or a rename never triggers a command.
    /// </summary>
    public bool IsValidShortcut =>
        Key != 0 && !IsModifierKey(Key) &&
        ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 || IsFunctionKey(Key));

    public static bool IsFunctionKey(int key) => key is >= 0x70 and <= 0x87;

    /// <summary>Parses an in-app shortcut ("F5", "Ctrl+Shift+N", "Alt+Left").</summary>
    public static bool TryParseShortcut(string? text, out HotkeyGesture gesture) =>
        TryParseAny(text, out gesture) && gesture.IsValidShortcut;

    public static bool TryParse(string? text, out HotkeyGesture gesture) =>
        TryParseAny(text, out gesture) && gesture.IsValid;

    private static bool TryParseAny(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Ctrl; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; continue;
            }
            if (key != 0) return false;
            key = KeyFromName(raw);
            if (key == 0) return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return key != 0;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    public static string KeyName(int key) => key switch
    {
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x1B => "Esc",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Del",
        >= 0x60 and <= 0x69 => "Num" + (key - 0x60),
        0x6A => "Num*",
        0x6B => "NumPlus", // not "Num+": '+' separates the parts of a gesture
        0x6D => "NumMinus",
        0x6F => "Num/",
        0xC0 => "`",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        _ => "0x" + key.ToString("X2"),
    };

    private static int KeyFromName(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "delete": return 0x2E;
            case "escape": return 0x1B;
            case "return": return 0x0D;
            case "pgup": return 0x21;
            case "pgdn": return 0x22;
        }
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return 0x6F + f;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int vk))
            return vk;

        for (int k = 0x08; k <= 0xFE; k++)
            if (k is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) && string.Equals(KeyName(k), name, StringComparison.OrdinalIgnoreCase))
                return k;
        return 0;
    }

    /// <summary>Shift / Ctrl / Alt / Win keys themselves (left, right and generic).</summary>
    public static bool IsModifierKey(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}
