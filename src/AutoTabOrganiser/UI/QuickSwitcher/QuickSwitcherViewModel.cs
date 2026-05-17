using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tree;
using AutoTabOrganiser.UI.ViewModels;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI.QuickSwitcher
{
    /// <summary>
    /// View-model for the Ctrl+T quick switcher. Lists all tabs (open ones first), filters
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
                                      Func<string, string, Task> openTabAtText)
        {
            _store = store;
            _settings = settings;
            _log = log;
            _openTabAtText = openTabAtText;
            Refresh();
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
                    var rows = await Task.Run(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var query = SearchQueryParser.Parse(input);
                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var (where, pars) = SearchQueryParser.ToSql(
                            query, nowMs,
                            includeContentInDefault: true,
                            ftsAvailable: _store.FtsAvailable);
                        // Frecency ordering: currently-open tabs first; then by access_count
                        // (how often the user has touched this tab); then by recency. A tab the
                        // user opens daily ranks above a tab they touched once last week.
                        return _store.ListTabs(where, pars, "is_open DESC, access_count DESC, ts DESC");
                    }, ct).ConfigureAwait(true);

                    if (ct.IsCancellationRequested) return;

                    var overrides = _settings?.Load()?.Ui?.TagColours;
                    Results.Clear();
                    foreach (var t in rows.Take(200))
                        Results.Add(new TabRowViewModel(t, overrides));
                    Selected = Results.FirstOrDefault();

                    StatusText = string.IsNullOrEmpty(input)
                        ? $"{Results.Count} tab(s)"
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
                    PreviewText = ExtractPreviewLines(raw, 12);
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

        private static string ExtractPreviewLines(string text, int max)
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
            var taken = lines.Skip(first).Take(max).ToArray();
            return string.Join(Environment.NewLine, taken);
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
