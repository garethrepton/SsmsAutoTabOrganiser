using System;
using System.IO;
using System.Threading;

namespace AutoTabOrganiser.Util
{
    /// <summary>
    /// Tiny in-process logger. Writes to %APPDATA%\AutoTabOrganiser\logs\YYYY-MM-DD.log
    /// and forwards to an Output-pane callback. Levels: Debug, Info, Warn, Error.
    /// </summary>
    internal sealed class Logger
    {
        private readonly object _gate = new object();
        private readonly string _dir;
        private readonly Action<string> _onPane;
        private readonly bool _debugEnabled;

        /// <summary>Daily log files older than this are deleted at construction time.</summary>
        private const int LogRetentionDays = 30;

        public Logger(string logDir, Action<string> onPane, bool debugEnabled = false)
        {
            _dir = logDir;
            _onPane = onPane;
            _debugEnabled = debugEnabled;
            try { Directory.CreateDirectory(_dir); } catch { }
            PruneOldLogs();
        }

        /// <summary>
        /// Delete daily logs older than <see cref="LogRetentionDays"/>. Without this the
        /// logs folder grows forever — and since git output (full diffs of query text) is
        /// logged, unbounded retention also means query content accumulating on disk.
        /// </summary>
        private void PruneOldLogs()
        {
            try
            {
                var threshold = DateTime.UtcNow.AddDays(-LogRetentionDays);
                foreach (var f in Directory.GetFiles(_dir, "*.log"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < threshold) File.Delete(f); }
                    catch { }
                }
            }
            catch { /* best-effort — logger must never throw */ }
        }

        public void Debug(string msg) { if (_debugEnabled) Write("DBG", msg); }
        public void Info (string msg) => Write("INF", msg);
        public void Warn (string msg) => Write("WRN", msg);
        public void Error(string msg) => Write("ERR", msg);
        public void Error(string msg, Exception ex) => Write("ERR", msg + " :: " + ex.GetType().Name + ": " + ex.Message);

        private void Write(string level, string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {level} {msg}";
            try { _onPane?.Invoke(line); } catch { }

            try
            {
                lock (_gate)
                {
                    var file = Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
            catch { /* swallow — logger must never throw */ }
        }
    }
}
