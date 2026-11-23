using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>A global hotkey parsed from text such as "Ctrl+Alt+Q" or "Ctrl+Shift+F9".</summary>
internal readonly record struct HotkeyBinding(uint Modifiers, Keys Key, string Text)
{
    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint modifiers = 0;
        Keys key = Keys.None;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default:
                    if (key != Keys.None)
                        return false; // two non-modifier keys
                    string name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    // Enum.TryParse also accepts bare numbers; a key must be named.
                    if (int.TryParse(name, out _) || !Enum.TryParse(name, ignoreCase: true, out key) || key == Keys.None)
                        return false;
                    break;
            }
        }

        // A hotkey without a modifier would swallow that key in every other app, including the browser.
        if (key == Keys.None || modifiers == 0)
            return false;

        binding = new HotkeyBinding(modifiers, key, text.Trim());
        return true;
    }
}
