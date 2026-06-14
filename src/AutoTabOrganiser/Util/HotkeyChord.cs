using System;
using System.Windows.Input;

namespace AutoTabOrganiser.Util
{
    /// <summary>
    /// Parses a human-written keyboard chord like "Ctrl+Shift+Q" into WPF
    /// <see cref="ModifierKeys"/> + <see cref="Key"/>, so the Quick Switcher hotkey can live in
    /// settings.json. Modifiers (Ctrl/Control, Shift, Alt — any order, case-insensitive) plus
    /// exactly one non-modifier key, '+'-separated. Common punctuation keys map to their Oem
    /// equivalents. Returns false for blank/invalid input (caller treats that as "no hotkey").
    /// </summary>
    internal static class HotkeyChord
    {
        public static bool TryParse(string chord, out ModifierKeys modifiers, out Key key)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(chord)) return false;

            foreach (var raw in chord.Split('+'))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= ModifierKeys.Control; continue;
                    case "shift":   modifiers |= ModifierKeys.Shift;   continue;
                    case "alt":     modifiers |= ModifierKeys.Alt;     continue;
                    case "win":
                    case "windows": modifiers |= ModifierKeys.Windows; continue;
                }

                // Non-modifier token — this is the key. Only one is allowed.
                if (key != Key.None) return false;
                if (!TryParseKey(token, out key)) return false;
            }

            // A chord must have a key; a modifier-only chord is meaningless here.
            return key != Key.None;
        }

        private static bool TryParseKey(string token, out Key key)
        {
            // Single digit -> D0..D9 (the WPF enum names the top-row digits this way).
            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
                return Enum.TryParse("D" + token, out key);

            switch (token)
            {
                case ";": key = Key.OemSemicolon; return true;
                case ",": key = Key.OemComma;     return true;
                case ".": key = Key.OemPeriod;    return true;
                case "/": key = Key.OemQuestion;  return true;
                case "'": key = Key.OemQuotes;    return true;
                case "[": key = Key.OemOpenBrackets;  return true;
                case "]": key = Key.OemCloseBrackets; return true;
                case "\\": key = Key.OemBackslash; return true;
                case "-": key = Key.OemMinus;     return true;
                case "=": key = Key.OemPlus;      return true;
                case "`": key = Key.OemTilde;     return true;
            }

            // Letters (A..Z), function keys (F1..), and named keys (Space, Enter, …) all parse
            // directly off the Key enum, case-insensitively.
            return Enum.TryParse(token, true, out key);
        }
    }
}
