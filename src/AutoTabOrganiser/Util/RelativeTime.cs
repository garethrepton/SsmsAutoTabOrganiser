using System;

namespace AutoTabOrganiser.Util
{
    /// <summary>
    /// Compact "X ago" formatting for Unix-millisecond timestamps. Tuned for the tool window:
    /// short enough to fit in a list-row corner, granular enough to distinguish recent edits.
    /// </summary>
    internal static class RelativeTime
    {
        public static string Format(long unixMs)
        {
            var now = DateTimeOffset.UtcNow;
            var then = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            var span = now - then;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;     // clock skew; clamp

            if (span.TotalSeconds < 45)  return "just now";
            if (span.TotalSeconds < 90)  return "1m";
            if (span.TotalMinutes < 60)  return ((int)span.TotalMinutes) + "m";
            if (span.TotalMinutes < 90)  return "1h";
            if (span.TotalHours   < 24)  return ((int)span.TotalHours) + "h";
            if (span.TotalHours   < 36)  return "1d";
            if (span.TotalDays    < 7)   return ((int)span.TotalDays) + "d";
            if (span.TotalDays    < 30)  return ((int)(span.TotalDays / 7)) + "w";
            if (span.TotalDays    < 365) return ((int)(span.TotalDays / 30)) + "mo";
            return ((int)(span.TotalDays / 365)) + "y";
        }

        public static string FormatNullable(long? unixMs)
            => unixMs.HasValue ? Format(unixMs.Value) : null;
    }
}
