using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace AutoTabOrganiser.Editor
{
    /// <summary>
    /// Resolves a tag name to a stable display colour. Explicit overrides from settings
    /// (#RRGGBB hex) win; otherwise the tag is hashed to a hue on a fixed palette so the
    /// same tag always gets the same colour across sessions.
    /// </summary>
    internal static class TagColourResolver
    {
        // Muted palette tuned to read on both dark and light editor themes without screaming.
        private static readonly Color[] Palette = new[]
        {
            Color.FromRgb(0xE2, 0xC0, 0x8D), // amber
            Color.FromRgb(0x73, 0xC9, 0x91), // green
            Color.FromRgb(0x6F, 0xA8, 0xDC), // blue
            Color.FromRgb(0xC5, 0x86, 0xC0), // pink
            Color.FromRgb(0xE0, 0x6C, 0x75), // red
            Color.FromRgb(0xC6, 0x78, 0xDD), // purple
            Color.FromRgb(0x56, 0xB6, 0xC2), // teal
            Color.FromRgb(0xD1, 0x9A, 0x66), // ochre
            Color.FromRgb(0x98, 0xC3, 0x79), // sage
            Color.FromRgb(0xE5, 0xC0, 0x7B), // mustard
        };

        public static Brush Resolve(string tag, IDictionary<string, string> overrides)
        {
            if (string.IsNullOrEmpty(tag)) return Brushes.Gray;
            if (overrides != null && overrides.TryGetValue(tag, out var hex) && TryParseHex(hex, out var c))
                return Frozen(new SolidColorBrush(c));
            return Frozen(new SolidColorBrush(Palette[StableHash(tag) % Palette.Length]));
        }

        private static SolidColorBrush Frozen(SolidColorBrush b)
        {
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        private static bool TryParseHex(string hex, out Color c)
        {
            c = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            if (s.Length == 6)
                c = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            else
                c = Color.FromArgb((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            return true;
        }

        // FNV-1a 32-bit on the lowercase tag — stable across processes and runs.
        private static int StableHash(string tag)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < tag.Length; i++)
                {
                    char ch = tag[i];
                    if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
                    h ^= ch;
                    h *= 16777619;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }

}
