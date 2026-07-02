using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AutoTabOrganiser.Editor;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tree;
using AutoTabOrganiser.UI.ViewModels;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI.QuickSwitcher
{
    /// <summary>
    /// View-model for the quick switcher. Lists all tabs (open ones first), filters
    /// live as the user types — including FTS5 content matching — and exposes the activation
    /// callback the window invokes on Enter / double-click.
    /// </summary>
    internal sealed class QuickSwitcherViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly SnapshotStore _store;
        private readonly SettingsStore _settings;
        private readonly Logger _log;
        private readonly Func<string, string, Task> _openTabAtText;
        private readonly string _currentTabId;
        private DispatcherTimer _debounce;

        public ObservableCollection<TabRowViewModel> Results { get; } = new ObservableCollection<TabRowViewModel>();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value ?? "";
                Notify();
                UpdateTagSuggestions();
                DebounceRefresh();
            }
        }

        private TabRowViewModel _selected;
        public TabRowViewModel Selected
        {
            get => _selected;
            set { if (ReferenceEquals(_selected, value)) return; _selected = value; Notify(); SchedulePreviewLoad(); }
        }

        private string _previewHeader = "";
        public string PreviewHeader { get => _previewHeader; private set { if (_previewHeader == value) return; _previewHeader = value; Notify(); } }

        private string _previewText = "";
        public string PreviewText
        {
            get => _previewText;
            private set
            {
                if (_previewText == value) return;
                _previewText = value;
                Notify();
                Notify(nameof(HasPreview));
                Notify(nameof(NoPreview));
            }
        }

        public bool HasPreview => !string.IsNullOrEmpty(_previewText);
        public bool NoPreview  =>  string.IsNullOrEmpty(_previewText);

        private string _statusText = "";
        public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; Notify(); } }

        public QuickSwitcherViewModel(SnapshotStore store, SettingsStore settings, Logger log,
                                      Func<string, string, Task> openTabAtText, string currentTabId = null)
        {
            _store = store;
            _settings = settings;
            _log = log;
            _openTabAtText = openTabAtText;
            _currentTabId = currentTabId;
            LoadTagCounts();
            Refresh();
        }

        // ---- tag autocomplete ----

        public ObservableCollection<TagSuggestion> TagSuggestions { get; } = new ObservableCollection<TagSuggestion>();

        private bool _hasTagSuggestions;
        public bool HasTagSuggestions
        {
            get => _hasTagSuggestions;
            private set { if (_hasTagSuggestions == value) return; _hasTagSuggestions = value; Notify(); }
        }

        private List<KeyValuePair<string, int>> _allTagCounts;
        private IDictionary<string, string> _tagColourOverrides;

        // async void: same crash-guard pattern as Refresh.
        private async void LoadTagCounts()
        {
            try
            {
                var counts = await Task.Run(() => _store.GetTagCounts()).ConfigureAwait(true);
                _allTagCounts = counts;
                UpdateTagSuggestions(); // the user may already be mid-"#..." by the time this lands
            }
            catch (Exception ex)
            {
                try { _log?.Error("QuickSwitcher tag-count load failed", ex); } catch { }
            }
        }

        /// <summary>
        /// The trailing token of the input when it is a tag term still being typed:
        /// (everything before it, the tag prefix as typed ("#", "-#", "tag:", "-tag:"),
        /// the partial tag text). Null when the caret isn't in a tag token.
        /// </summary>
        private static (string head, string prefix, string partial)? ActiveTagToken(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (char.IsWhiteSpace(text[text.Length - 1])) return null; // token already finished
            int i = text.Length;
            while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
            var token = text.Substring(i);
            var head = text.Substring(0, i);
            var neg = token.StartsWith("-") ? "-" : "";
            var t = neg.Length > 0 ? token.Substring(1) : token;
            if (t.StartsWith("#")) return (head, neg + "#", t.Substring(1));
            if (t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) return (head, neg + t.Substring(0, 4), t.Substring(4));
            return null;
        }

        private void UpdateTagSuggestions()
        {
            TagSuggestions.Clear();
            var tok = ActiveTagToken(_searchText);
            if (tok == null || _allTagCounts == null || _allTagCounts.Count == 0)
            {
                HasTagSuggestions = false;
                return;
            }

            if (_tagColourOverrides == null)
                try { _tagColourOverrides = _settings?.Load()?.Ui?.TagColours; } catch { }

            var partial = tok.Value.partial;
            foreach (var kv in _allTagCounts) // already most-used-first
            {
                if (partial.Length > 0 && !kv.Key.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                TagSuggestions.Add(new TagSuggestion(
                    new TagChip(kv.Key, TagColourResolver.Resolve(kv.Key, _tagColourOverrides)), kv.Value));
                if (TagSuggestions.Count >= 12) break; // one strip row-ish; typing narrows further
            }
            HasTagSuggestions = TagSuggestions.Count > 0;
        }

        /// <summary>Replace the tag token being typed with <paramref name="s"/>, keeping the
        /// prefix style the user chose (#/tag:, negated or not). Trailing space finishes the
        /// token, which also dismisses the strip.</summary>
        public void AcceptTagSuggestion(TagSuggestion s)
        {
            if (s == null) return;
            var tok = ActiveTagToken(_searchText);
            SearchText = tok == null
                ? (_searchText ?? "").TrimEnd() + (string.IsNullOrWhiteSpace(_searchText) ? "" : " ") + "#" + s.Text + " "
                : tok.Value.head + tok.Value.prefix + s.Text + " ";
        }

        /// <summary>Tab-key path: accept the top suggestion. Returns false when the strip is
        /// not showing so the caller can leave the keystroke alone.</summary>
        public bool AcceptFirstTagSuggestion()
        {
            if (!HasTagSuggestions || TagSuggestions.Count == 0) return false;
            AcceptTagSuggestion(TagSuggestions[0]);
            return true;
        }

        /// <summary>Row-chip click path: append <paramref name="tag"/> as a #filter unless the
        /// query already carries it.</summary>
        public void AddTagFilter(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            var term = "#" + tag;
            var current = _searchText ?? "";
            if (current.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return;
            SearchText = string.IsNullOrWhiteSpace(current) ? term + " " : current.TrimEnd() + " " + term + " ";
        }

        /// <summary>Move selection by <paramref name="delta"/> rows, clamped to the list bounds.</summary>
        public void MoveSelection(int delta)
        {
            if (Results.Count == 0) { Selected = null; return; }
            var current = Selected == null ? -1 : Results.IndexOf(Selected);
            var next = current + delta;
            if (next < 0) next = 0;
            if (next >= Results.Count) next = Results.Count - 1;
            Selected = Results[next];
        }

        public void ActivateSelected()
        {
            var s = Selected;
            if (s == null) return;
            var tabId = s.Source?.TabId;
            if (string.IsNullOrEmpty(tabId)) return; // defensive: bogus row, nothing to open
            _ = _openTabAtText?.Invoke(tabId, ExtractFindText(_searchText));
        }

        /// <summary>Ctrl+1..9 path: jump straight to the row at <paramref name="index"/>.</summary>
        public void ActivateIndex(int index)
        {
            if (index < 0 || index >= Results.Count) return;
            Selected = Results[index];
            ActivateSelected();
        }

        /// <summary>
        /// Pull the bare (no-prefix, non-negated) tokens out of the search input so they can
        /// be used as a find-in-document target. Field-prefixed tokens (name:, tag:, content:,
        /// etc.) and negations are stripped — they describe filtering, not the literal text
        /// the user wants to land on.
        /// </summary>
        private static string ExtractFindText(string searchInput)
        {
            if (string.IsNullOrWhiteSpace(searchInput)) return null;
            var parsed = SearchQueryParser.Parse(searchInput);
            var bare = parsed.Terms
                .Where(term => !term.Negate && term.Field == null)
                .Select(term => term.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            if (bare.Count == 0) return null;
            // First token only — multi-token find is rarely useful and the longest single
            // contiguous string the user typed is the most likely target.
            return bare[0];
        }

        // ---- internals ----

        private void DebounceRefresh()
        {
            if (_debounce == null)
            {
                _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };
            }
            _debounce.Stop();
            _debounce.Start();
        }

        private CancellationTokenSource _refreshCts;

        // async void is unavoidable here (driven by a DispatcherTimer Tick handler that
        // discards the returned Task) but exceptions in async void route to the captured
        // SyncContext and crash the SSMS process. Wrap *everything*, including the prologue
        // and any logging in the inner catches, so no escape path exists.
        private async void Refresh()
        {
            try
            {
                // Cancel any previous in-flight search so we don't race a stale set of rows
                // on top of fresher results. The token also lets long-running FTS queries
                // get out of the way if the user keeps typing.
                _refreshCts?.Cancel();
                var cts = _refreshCts = new CancellationTokenSource();
                var ct = cts.Token;
                var input = _searchText ?? "";

                try
                {
                    var (rows, bare) = await Task.Run(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var query = SearchQueryParser.Parse(input);
                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var (where, pars) = SearchQueryParser.ToSql(
                            query, nowMs,
                            includeContentInDefault: true,
                            ftsAvailable: _store.FtsAvailable);
                        // MRU baseline: last focused/edited first (alt-tab semantics — the tab
                        // you just left sits at #2 even if something below it is still open).
                        // last_activated_ts is 0 on rows predating activation tracking, so MAX
                        // falls back to the snapshot time.
                        const string mruOrder = "MAX(last_activated_ts, ts) DESC, is_open DESC, access_count DESC";
                        var list = _store.ListTabs(where, pars, mruOrder);

                        var bareTerms = query.Terms
                            .Where(t => !t.Negate && t.Field == null)
                            .Select(t => t.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .ToList();

                        if (bareTerms.Count > 0)
                        {
                            // Fuzzy union: LIKE/FTS can't see subsequence hits ("cusord" →
                            // CustomerOrders), so re-query with only the field/negation terms
                            // and fuzzy-match names client-side, then rank everything by how
                            // well it matched.
                            var fieldQuery = new SearchQuery();
                            foreach (var t in query.Terms.Where(t => t.Field != null || t.Negate))
                                fieldQuery.Terms.Add(t);
                            var (w2, p2) = SearchQueryParser.ToSql(
                                fieldQuery, nowMs,
                                includeContentInDefault: false,
                                ftsAvailable: _store.FtsAvailable);
                            var have = new HashSet<string>(list.Select(t => t.TabId));
                            foreach (var c in _store.ListTabs(w2, p2, mruOrder))
                            {
                                if (have.Contains(c.TabId)) continue;
                                if (bareTerms.TrueForAll(b => QuickSwitchRanker.FuzzyMatches(c.Name, b)))
                                    list.Add(c);
                            }
                            list = QuickSwitchRanker.Rank(list, bareTerms);
                        }
                        return (list, bareTerms);
                    }, ct).ConfigureAwait(true);

                    if (ct.IsCancellationRequested) return;

                    var overrides = _settings?.Load()?.Ui?.TagColours;
                    Results.Clear();
                    foreach (var t in rows.Take(200))
                    {
                        var row = new TabRowViewModel(t, overrides);
                        if (bare.Count > 0) row.HighlightTerms = bare;
                        if (Results.Count < 9) row.ShortcutHint = (Results.Count + 1).ToString();
                        Results.Add(row);
                    }

                    // Alt-tab reflex: with no query the top row is the tab the user is already
                    // in — pre-select the row below so open → Enter flips to the previous tab.
                    if (string.IsNullOrWhiteSpace(input) && Results.Count > 1
                        && _currentTabId != null && Results[0].Source?.TabId == _currentTabId)
                        Selected = Results[1];
                    else
                        Selected = Results.FirstOrDefault();

                    StatusText = string.IsNullOrEmpty(input)
                        ? $"{Results.Count} tab(s), most recent first"
                        : $"{Results.Count} match(es)";
                }
                catch (OperationCanceledException) { /* superseded by a fresher search */ }
                catch (Exception ex)
                {
                    try { _log?.Error("QuickSwitcher refresh failed", ex); } catch { }
                    try
                    {
                        StatusText = "Search error: " + ex.Message;
                        Results.Clear();
                    }
                    catch { /* if even Clear/StatusText blow up, swallow rather than crash */ }
                }
            }
            catch (Exception outer)
            {
                // Last-resort guard: anything escaping the inner try (CTS dispose race, the
                // catch handlers themselves throwing, etc.) must not become an unhandled
                // async-void exception.
                try { _log?.Error("QuickSwitcher refresh outer failure", outer); } catch { }
            }
        }

        // ---- preview pane ----

        private DispatcherTimer _previewDebounce;
        private CancellationTokenSource _previewCts;
        private int _previewLoadSeq;

        private void SchedulePreviewLoad()
        {
            if (_previewDebounce == null)
            {
                _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _previewDebounce.Tick += (s, e) => { _previewDebounce.Stop(); LoadPreviewForSelected(); };
            }
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        // async void: same pattern as Refresh — guarded by an outer try so an escaping
        // exception can't crash the SSMS process via the async-void SyncContext path.
        private async void LoadPreviewForSelected()
        {
            try
            {
                var sel = _selected;
                if (sel == null) { PreviewHeader = ""; PreviewText = ""; return; }
                var summary = sel.Source;
                if (summary == null) { PreviewHeader = ""; PreviewText = ""; return; }

                var folder = string.IsNullOrEmpty(summary.Folder) ? "Unfiled" : summary.Folder;
                var name   = string.IsNullOrEmpty(summary.Name)   ? "(unnamed)" : summary.Name;
                PreviewHeader = folder + " / " + name;

                var snapshotId = summary.LatestSnapshotId;
                if (string.IsNullOrEmpty(snapshotId)) { PreviewText = ""; return; }

                _previewCts?.Cancel();
                var cts = _previewCts = new CancellationTokenSource();
                var ct  = cts.Token;
                var seq = ++_previewLoadSeq;

                try
                {
                    var raw = await Task.Run(() => _store.ReadSnapshotContentById(snapshotId), ct).ConfigureAwait(true);
                    if (ct.IsCancellationRequested || seq != _previewLoadSeq) return;
                    // When the user typed bare search words, centre the preview window on the
                    // first occurrence so a content (FTS) hit shows the matching lines rather
                    // than the top of the file.
                    PreviewText = ExtractPreviewLines(raw, 12, ExtractFindText(_searchText));
                }
                catch (OperationCanceledException) { /* superseded */ }
                catch (Exception ex)
                {
                    try { _log?.Error("QuickSwitcher preview load failed", ex); } catch { }
                    PreviewText = "";
                }
            }
            catch (Exception outer)
            {
                try { _log?.Error("QuickSwitcher preview outer failure", outer); } catch { }
            }
        }

        private static string ExtractPreviewLines(string text, int max, string findText = null)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var meta = MetadataParser.Parse(text);
            var body = meta.CommentBlockEndExclusive < text.Length
                ? text.Substring(meta.CommentBlockEndExclusive)
                : "";
            if (string.IsNullOrEmpty(body)) return "";
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var first = 0;
            while (first < lines.Length && string.IsNullOrWhiteSpace(lines[first])) first++;

            // Content-hit mode: window the preview around the first line containing the
            // typed text, with a little leading context. Falls through to top-of-body when
            // the text doesn't appear (e.g. the hit was on name/tag instead of content).
            if (!string.IsNullOrWhiteSpace(findText))
            {
                for (int i = first; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(findText, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var start = Math.Max(first, i - 3);
                    var window = lines.Skip(start).Take(max).ToArray();
                    // Mark the hit line so the eye lands on it instantly (the window gives it
                    // at most 3 lines of leading context, so it's always inside `window`).
                    window[i - start] = "▶ " + window[i - start];
                    var prefixNote = start > first ? "… (line " + (i - first + 1) + ")" + Environment.NewLine : "";
                    return prefixNote + string.Join(Environment.NewLine, window);
                }
            }

            var taken = lines.Skip(first).Take(max).ToArray();
            return string.Join(Environment.NewLine, taken);
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
