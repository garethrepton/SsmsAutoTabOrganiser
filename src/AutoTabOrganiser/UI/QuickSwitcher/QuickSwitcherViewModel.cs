using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
            set { if (ReferenceEquals(_selected, value)) return; _selected = value; Notify(); }
        }

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
            _ = _openTabAtText?.Invoke(s.Source?.TabId, ExtractFindText(_searchText));
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
                        // Open tabs first, then most-recent first.
                        return _store.ListTabs(where, pars, "is_open DESC, ts DESC");
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

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
