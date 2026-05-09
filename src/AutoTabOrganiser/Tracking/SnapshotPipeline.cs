using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Tracking
{
    /// <summary>
    /// Per-document snapshot bookkeeping. The DocumentTracker drives this externally by polling
    /// the document text and calling <see cref="OfferSnapshot"/>; the pipeline then deduplicates,
    /// writes to storage, and fires the tab-updated callback. Saved/closed events bypass dedup.
    /// </summary>
    internal sealed class SnapshotPipeline : IDisposable
    {
        private readonly string _moniker;
        private readonly Func<string> _getWindowTitle;
        private readonly SnapshotStore _store;
        private readonly SettingsStore _settings;
        private readonly Logger _log;
        private readonly Action<string> _onTabUpdated;
        private readonly Action<string> _onTabClosed;
        private readonly Func<string, string, ParsedMetadata, bool> _tryInjectId;
        private readonly Func<List<string>, bool> _tryInjectAutoTags;

        private string _resolvedTabId;
        private bool _idInjectionAttempted;
        private string _lastSnapshotHash;
        private long _lastSnapshotTickMs;
        private string _pendingHash;
        private long _pendingFirstSeenMs;
        private bool _disposed;

        public string Moniker => _moniker;
        public string TabId => _resolvedTabId;

        public SnapshotPipeline(string moniker, Func<string> getWindowTitle,
                                SnapshotStore store, SettingsStore settings, Logger log,
                                Action<string> onTabUpdated, Action<string> onTabClosed,
                                Func<string, string, ParsedMetadata, bool> tryInjectId,
                                Func<List<string>, bool> tryInjectAutoTags)
        {
            _moniker = moniker;
            _getWindowTitle = getWindowTitle;
            _store = store;
            _settings = settings;
            _log = log;
            _onTabUpdated = onTabUpdated;
            _onTabClosed = onTabClosed;
            _tryInjectId = tryInjectId;
            _tryInjectAutoTags = tryInjectAutoTags;
        }

        /// <summary>
        /// Called from the polling loop with the current document text. Decides whether to
        /// snapshot now (hash changed AND debounce window elapsed) or wait.
        /// </summary>
        public void OfferSnapshot(string text, string reason)
        {
            if (_disposed || text == null) return;

            var settings = _settings.Load();
            var meta = MetadataParser.Parse(text);
            if (meta.NoSnapshot) return;

            var (server, database) = ConnectionExtractor.FromWindowTitle(_getWindowTitle?.Invoke());

            // Fall back to the file's @server / @database if the SSMS title didn't expose
            // a connection (e.g. the user opened a saved script and hasn't connected yet).
            // The live title still wins when present.
            if (string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(meta.Server)) server = meta.Server;
            if (string.IsNullOrEmpty(database) && !string.IsNullOrEmpty(meta.Database)) database = meta.Database;

            if (IsServerDenied(server, settings)) return;

            var hash = Hashing.Sha256Hex(text);
            var nowMs = Environment.TickCount;

            // saved/closed/first: bypass dedup and debounce. "first" is fired by the tracker as
            // soon as a tab attaches, so a brand-new tab lands in the side panel immediately
            // rather than after the edit-debounce window. Skip "first" on empty content so a
            // blank untitled tab doesn't create a useless side-panel entry.
            bool force = reason == "saved" || reason == "closed" || reason == "first";
            if (reason == "first" && string.IsNullOrWhiteSpace(text)) return;

            // First-time write for this tab: also force-immediate. Catches the brand-new
            // untitled tab that was empty at attach (so the "first" reason was skipped) and
            // now has content via polling — without this it sits in the debounce window for
            // ~5s before appearing in the side panel.
            if (_lastSnapshotHash == null && !string.IsNullOrWhiteSpace(text)) force = true;

            if (!force && string.Equals(hash, _lastSnapshotHash, StringComparison.Ordinal))
            {
                // No change since last snapshot.
                _pendingHash = null;
                return;
            }

            if (!force)
            {
                if (!string.Equals(hash, _pendingHash, StringComparison.Ordinal))
                {
                    _pendingHash = hash;
                    _pendingFirstSeenMs = nowMs;
                    return;
                }

                // Floor was 2s — too slow when iterating on a stored query. 200ms is the
                // smallest setting that doesn't make the SQLite index churn on every keystroke.
                var debounceMs = Math.Max(200, settings.Snapshotting.EditDebounceSeconds * 1000);
                var flushMs    = Math.Max(15000, settings.Snapshotting.FlushIntervalSeconds * 1000);

                bool stableLongEnough = (nowMs - _pendingFirstSeenMs) >= debounceMs;
                bool flushDue         = _lastSnapshotTickMs != 0 && (nowMs - _lastSnapshotTickMs) >= flushMs;
                if (!stableLongEnough && !flushDue) return;
                if (flushDue) reason = "flush";
            }

            WriteSnapshot(text, hash, meta, reason, server, database);
        }

        public void OnSaved(string text)  => OfferSnapshot(text ?? string.Empty, "saved");
        public void OnClosed(string text)
        {
            try { OfferSnapshot(text ?? string.Empty, "closed"); }
            finally { _onTabClosed?.Invoke(_resolvedTabId); }
        }

        private void WriteSnapshot(string text, string contentHash, ParsedMetadata meta,
                                   string reason, string server, string database)
        {
            try
            {
                var tabId = ResolveTabId(meta, text);
                var nameFallback = meta.Name ?? FallbackName(text, _moniker);

                // Auto-tags: rules from settings.json that mark the snapshot when a substring is
                // found in the document. Merge into meta.Tags (deduped, preserve order).
                var settingsNow = _settings.Load();
                var autoTags = AutoTagger.MatchedTags(text, settingsNow.Snapshotting.AutoTagRules);
                var combinedTags = new List<string>(meta.Tags);
                foreach (var t in autoTags)
                    if (!combinedTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                        combinedTags.Add(t);

                var record = new SnapshotRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TabId = tabId,
                    FilePath = IsRealPath(_moniker) ? _moniker : null,
                    Folder = string.IsNullOrEmpty(meta.Folder) ? "Unfiled" : meta.Folder,
                    Name = nameFallback,
                    ContentHash = contentHash,
                    Reason = reason,
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Server = server,
                    Database = database,
                    Tags = combinedTags,
                    Desc = meta.Description
                };

                var written = _store.WriteSnapshot(record, text);
                _lastSnapshotHash = contentHash;
                _lastSnapshotTickMs = Environment.TickCount;
                _pendingHash = null;

                _log.Info($"[snap {reason}] {nameFallback}  tags={record.Tags.Count}  -> {written}");
                _onTabUpdated?.Invoke(tabId);

                if (settingsNow.Snapshotting.AutoInjectId && string.IsNullOrEmpty(meta.Id) && !_idInjectionAttempted)
                {
                    _idInjectionAttempted = true;
                    _tryInjectId?.Invoke(tabId, _moniker, meta);
                }

                // Inject newly-matched auto-tags into the file header (only the ones that aren't
                // already present). Honours the same "must have a comment block" guard as @id.
                if (settingsNow.Snapshotting.AutoTagInjectIntoHeader && autoTags.Count > 0)
                {
                    var newOnly = autoTags.Where(t => !meta.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (newOnly.Count > 0)
                    {
                        _tryInjectAutoTags?.Invoke(newOnly);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"snapshot write failed for {_moniker}", ex);
            }
        }

        private string ResolveTabId(ParsedMetadata meta, string text)
        {
            if (!string.IsNullOrEmpty(meta.Id)) { _resolvedTabId = meta.Id; return _resolvedTabId; }
            if (!string.IsNullOrEmpty(_resolvedTabId)) return _resolvedTabId;

            var fp = Hashing.Fingerprint(text);
            var minTs = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds();
            var found = _store.FindTabIdByFingerprint(fp, minTs);
            _resolvedTabId = found ?? MetadataWriter.GenerateShortId();
            return _resolvedTabId;
        }

        private static bool IsServerDenied(string server, AppSettings s)
        {
            if (string.IsNullOrEmpty(server)) return false;
            foreach (var pat in s.Privacy.ServerDenyList ?? new List<string>())
                if (Match(server, pat)) return true;

            if (s.Privacy.ServerAllowList != null && s.Privacy.ServerAllowList.Count > 0)
            {
                foreach (var pat in s.Privacy.ServerAllowList) if (Match(server, pat)) return false;
                return true;
            }
            return false;
        }
        private static bool Match(string s, string p) => !string.IsNullOrEmpty(p) && s.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsRealPath(string moniker)
        {
            if (string.IsNullOrEmpty(moniker)) return false;
            try { return System.IO.Path.IsPathRooted(moniker); } catch { return false; }
        }

        private static string FallbackName(string text, string moniker)
        {
            var firstComment = FirstCommentText(text);
            if (!string.IsNullOrEmpty(firstComment))
            {
                return Truncate(firstComment, 60);
            }

            var sanitised = PathSanitiser.FromFirstLine(text);
            if (!string.IsNullOrEmpty(sanitised)) return sanitised;

            try { return System.IO.Path.GetFileNameWithoutExtension(moniker) ?? "untitled"; }
            catch { return "untitled"; }
        }

        // First leading-block comment line whose content isn't a @key metadata line.
        private static string FirstCommentText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0, len = text.Length;
            while (i < len)
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                var line = text.Substring(start, i - start).TrimEnd('\r');
                if (i < len) i++;
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0) continue;
                if (!trimmed.StartsWith("--")) return null;        // hit code; stop searching
                var content = trimmed.Substring(2).TrimStart().TrimEnd();
                if (content.Length == 0) continue;
                if (content.StartsWith("@")) continue;             // @folder / @name / @desc etc. — not a display name
                return content;
            }
            return null;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        public void Dispose() { _disposed = true; }
    }
}
