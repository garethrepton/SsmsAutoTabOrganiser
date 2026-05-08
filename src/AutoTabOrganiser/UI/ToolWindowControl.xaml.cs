using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tree;
using AutoTabOrganiser.UI.Detail;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI
{
    internal partial class ToolWindowControl : UserControl
    {
        private SnapshotStore _store;
        private Func<string, Task> _openTabId;
        private Action _onSettingsClick;
        private Logger _log;
        private SettingsStore _settings;
        private DetailPane _detailPane;
        private List<string> _pinnedTags = new List<string>();
        private readonly Git.GitStatusResolver _gitResolver = new Git.GitStatusResolver();
        private readonly Dictionary<string, Git.GitFileStatus> _gitStatusByPath = new Dictionary<string, Git.GitFileStatus>(StringComparer.OrdinalIgnoreCase);

        public ToolWindowControl()
        {
            InitializeComponent();

            SearchBox.TextChanged += (s, e) =>
            {
                SearchClearBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                    ? Visibility.Collapsed : Visibility.Visible;
                DebounceMainSearch();
            };
            SortModeBox.SelectionChanged += (s, e) => RefreshTabs();
            GearButton.Click += (s, e) => _onSettingsClick?.Invoke();

            TabsList.MouseDoubleClick += OnListDoubleClick;
            RecentList.MouseDoubleClick += OnRecentDoubleClick;
            TabsList.SelectionChanged += (s, e) => OnSelectionChanged((TabsList.SelectedItem as TabRowVm)?.Source);
            RecentList.SelectionChanged += (s, e) => OnSelectionChanged((RecentList.SelectedItem as TabRowVm)?.Source);

            _detailPane = new DetailPane();
            DetailHost.Content = _detailPane;

            // Safety net: even if onTabUpdated callbacks miss, the panel stays current.
            _autoRefresh = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoRefresh.Tick += (s, e) => { RefreshTabs(); RefreshAllPinSections(); RefreshSnippets(); };
            this.Loaded += (s, e) => _autoRefresh.Start();
            this.Unloaded += (s, e) => _autoRefresh.Stop();
        }

        private DispatcherTimer _autoRefresh;

        public void Initialise(SnapshotStore store, Func<string, Task> openTabId, Action onSettingsClick,
                               Logger log, SettingsStore settings, string viewMode, string sortMode)
        {
            _store = store;
            _openTabId = openTabId;
            _onSettingsClick = onSettingsClick;
            _log = log;
            _settings = settings;
            SelectByTag(SortModeBox, sortMode ?? "recent");
            LoadPinnedTags();
            RefreshTabs();
            RefreshRecent();
            RefreshSnippets();
            StoragePathText.Text = _store?.Root ?? "(unknown)";
            StoragePathLabel.ToolTip = _store?.Root;
        }

        private void OnOpenStorage_Click(object sender, RoutedEventArgs e)
        {
            var path = _store?.Root;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _log?.Error("Open storage folder failed", ex); }
        }

        // -------- pinned tags --------

        // Debounce timer for main search.
        private DispatcherTimer _mainSearchDebounce;
        private readonly Dictionary<string, ListBox> _pinSections =
            new Dictionary<string, ListBox>(StringComparer.OrdinalIgnoreCase);

        private void LoadPinnedTags()
        {
            _pinnedTags = (_settings?.Load().Ui.PinnedTags ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            RebuildPinnedSections();
        }

        private void SavePinnedTags()
        {
            _settings?.Mutate(s => s.Ui.PinnedTags = new List<string>(_pinnedTags));
        }

        private void RebuildPinnedSections()
        {
            PinnedSectionsHost.Children.Clear();
            _pinSections.Clear();
            foreach (var tag in _pinnedTags)
            {
                PinnedSectionsHost.Children.Add(BuildPinSection(tag));
            }
        }

        private FrameworkElement BuildPinSection(string tag)
        {
            var expander = new Expander { IsExpanded = true, Margin = new Thickness(4, 2, 4, 2) };

            // Header: #tag    [✕]
            var headerPanel = new DockPanel { LastChildFill = false };
            var label = new TextBlock { Text = "#" + tag, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            headerPanel.Children.Add(label);
            var unpin = new Button
            {
                Content = "✕", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 0, ToolTip = "Unpin"
            };
            unpin.Click += (s, e) =>
            {
                _pinnedTags.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
                SavePinnedTags();
                RebuildPinnedSections();
            };
            DockPanel.SetDock(unpin, Dock.Right);
            headerPanel.Children.Add(unpin);
            expander.Header = headerPanel;

            var list = new ListBox
            {
                Margin = new Thickness(8, 4, 8, 4),
                MaxHeight = 240,
                BorderThickness = new Thickness(0),
                ItemTemplate = (DataTemplate)this.Resources["TabRowTemplate"],
                ItemContainerStyle = (Style)this.Resources["ItemContainerWithMenu"]
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            list.MouseDoubleClick += (s, e) =>
            {
                if (list.SelectedItem is TabRowVm vm) _ = _openTabId?.Invoke(vm.Source.TabId);
            };
            list.SelectionChanged += (s, e) =>
            {
                if (list.SelectedItem is TabRowVm vm) OnSelectionChanged(vm.Source);
            };

            expander.Content = list;

            _pinSections[tag] = list;
            RefreshPinSection(tag);
            return expander;
        }

        // Row template and container style are defined in XAML and shared via Resources;
        // BuildPinSection picks them up via FindResource. The legacy code-built versions were
        // removed in favour of the XAML one-source-of-truth (icons, chips, hover-action).

        private void DebounceMainSearch()
        {
            if (_mainSearchDebounce == null)
            {
                _mainSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _mainSearchDebounce.Tick += (s, e) => { _mainSearchDebounce.Stop(); RefreshTabs(); };
            }
            _mainSearchDebounce.Stop();
            _mainSearchDebounce.Start();
        }

        private void RefreshPinSection(string tag)
        {
            if (_store == null) return;
            if (!_pinSections.TryGetValue(tag, out var list)) return;
            try
            {
                var query = SearchQueryParser.Parse("#" + tag);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var (where, pars) = SearchQueryParser.ToSql(query, nowMs);
                var tabs = _store.ListTabs(where, pars, "ts DESC");
                var rows = tabs.Select(t => MakeRowVm(t)).ToList();
                list.ItemsSource = rows;
                KickGitStatusUpdate(rows);
            }
            catch (Exception ex)
            {
                _log?.Error("RefreshPinSection failed for #" + tag, ex);
            }
        }

        private void RefreshAllPinSections()
        {
            foreach (var tag in _pinnedTags) RefreshPinSection(tag);
        }

        private void OnPinNewTag_Click(object sender, RoutedEventArgs e)
        {
            var allTags = _store?.GetAllTags() ?? new List<string>();
            var picked = ShowTagPickerPopup(allTags, _pinnedTags, sender as Button);
            if (picked == null) return;
            _pinnedTags = picked.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SavePinnedTags();
            RebuildPinnedSections();
        }

        // Inline lightweight popup with a checkbox per known tag plus a free-form add box.
        private static List<string> ShowTagPickerPopup(List<string> allTags, List<string> pinned, UIElement anchor)
        {
            var result = new List<string>(pinned);
            var win = new Window
            {
                Title = "Pin tags",
                Width = 280, Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(anchor) ?? Application.Current?.MainWindow
            };

            var dock = new DockPanel();
            var addPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            DockPanel.SetDock(addPanel, Dock.Top);
            var addBox = new TextBox { Width = 160, Margin = new Thickness(0, 0, 4, 0) };
            var addBtn = new Button { Content = "Add", MinWidth = 60 };
            addPanel.Children.Add(new TextBlock { Text = "New tag:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            addPanel.Children.Add(addBox);
            addPanel.Children.Add(addBtn);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 60, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 60 };
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);

            var list = new ListBox { Margin = new Thickness(8), SelectionMode = SelectionMode.Multiple };
            var allMerged = new List<string>(allTags);
            foreach (var p in pinned) if (!allMerged.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))) allMerged.Add(p);
            allMerged.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var t in allMerged)
            {
                var cb = new CheckBox { Content = "#" + t, IsChecked = pinned.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase)) };
                cb.Tag = t;
                list.Items.Add(cb);
            }

            addBtn.Click += (s, e) =>
            {
                var raw = (addBox.Text ?? "").Trim().TrimStart('#');
                if (string.IsNullOrEmpty(raw)) return;
                var existing = list.Items.OfType<CheckBox>().FirstOrDefault(c => string.Equals(c.Tag as string, raw, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { existing.IsChecked = true; return; }
                var cb = new CheckBox { Content = "#" + raw, IsChecked = true, Tag = raw };
                list.Items.Insert(0, cb);
                addBox.Text = "";
            };

            dock.Children.Add(addPanel);
            dock.Children.Add(btnPanel);
            dock.Children.Add(list);
            win.Content = dock;

            List<string> picked = null;
            ok.Click += (s, e) =>
            {
                picked = list.Items.OfType<CheckBox>()
                    .Where(c => c.IsChecked == true)
                    .Select(c => (string)c.Tag)
                    .ToList();
                win.DialogResult = true;
            };

            return win.ShowDialog() == true ? picked : null;
        }

        private void OnCtx_PinTagOpened(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            if (mi == null) return;
            mi.Items.Clear();
            var t = GetContextTarget(sender);
            if (t == null || string.IsNullOrEmpty(t.TagsCsv))
            {
                mi.Items.Add(new MenuItem { Header = "(no tags on this row)", IsEnabled = false });
                return;
            }
            foreach (var tag in t.TagsCsv.Split(','))
            {
                var item = new MenuItem
                {
                    Header = "#" + tag,
                    IsCheckable = true,
                    IsChecked = _pinnedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
                };
                var captured = tag;
                item.Click += (s, ev) =>
                {
                    if (_pinnedTags.Contains(captured, StringComparer.OrdinalIgnoreCase))
                        _pinnedTags.RemoveAll(x => string.Equals(x, captured, StringComparison.OrdinalIgnoreCase));
                    else
                        _pinnedTags.Add(captured);
                    SavePinnedTags();
                    RebuildPinnedSections();
                };
                mi.Items.Add(item);
            }
        }

        // ViewMode is fixed to "list" now that the tree view has been removed; the property is
        // retained so external callers (package wiring) keep compiling without a churn.
        public string ViewMode => "list";
        public string SortMode => (SortModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "recent";

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                { item.IsSelected = true; return; }
        }

        public void RefreshTabs()
        {
            if (_store == null) return;
            try
            {
                var query = SearchQueryParser.Parse(SearchBox.Text ?? "");
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var (where, pars) = SearchQueryParser.ToSql(query, nowMs);
                string orderBy = SortToOrderBy(SortMode);

                var tabs = _store.ListTabs(where, pars, orderBy);

                var rowVms = tabs.Select(t => MakeRowVm(t)).ToList();
                TabsList.ItemsSource = rowVms;
                TabsEmpty.Visibility = tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                KickGitStatusUpdate(rowVms);
                RefreshRecent();
            }
            catch (Exception ex)
            {
                _detailPane?.ShowError(ex.Message);
            }
        }

        private TabRowVm MakeRowVm(TabSummary t)
        {
            var overrides = _settings?.Load()?.Ui?.TagColours;
            var vm = new TabRowVm(t, overrides);
            // Look up cached git status if we have it for this tab's file path.
            var path = LookupOriginalFilePath(t);
            if (!string.IsNullOrEmpty(path) && _gitStatusByPath.TryGetValue(path, out var st)) vm.GitStatus = st;
            return vm;
        }

        private void KickGitStatusUpdate(IEnumerable<TabRowVm> rowVms)
        {
            // Collect file paths off-UI; resolve git status; marshal back; refresh visible items.
            var paths = new List<string>();
            foreach (var vm in rowVms)
            {
                var p = LookupOriginalFilePath(vm.Source);
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            if (paths.Count == 0) return;

            Task.Run(() =>
            {
                Dictionary<string, Git.GitFileStatus> map;
                try { map = _gitResolver.Resolve(paths); } catch { return; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var kv in map) _gitStatusByPath[kv.Key] = kv.Value;
                    // Touch the rows to recompute Marker.
                    if (TabsList.ItemsSource is IEnumerable<TabRowVm> visible)
                    {
                        foreach (var vm in visible)
                        {
                            var p = LookupOriginalFilePath(vm.Source);
                            if (!string.IsNullOrEmpty(p) && _gitStatusByPath.TryGetValue(p, out var st))
                                vm.GitStatus = st;
                        }
                    }
                    foreach (var vm in (RecentList.ItemsSource as IEnumerable<TabRowVm>) ?? Enumerable.Empty<TabRowVm>())
                    {
                        var p = LookupOriginalFilePath(vm.Source);
                        if (!string.IsNullOrEmpty(p) && _gitStatusByPath.TryGetValue(p, out var st))
                            vm.GitStatus = st;
                    }
                    foreach (var list in _pinSections.Values)
                    {
                        if (list.ItemsSource is IEnumerable<TabRowVm> rows)
                            foreach (var vm in rows)
                            {
                                var p = LookupOriginalFilePath(vm.Source);
                                if (!string.IsNullOrEmpty(p) && _gitStatusByPath.TryGetValue(p, out var st))
                                    vm.GitStatus = st;
                            }
                    }
                    // Force ItemTemplate re-render by reassigning ItemsSource.
                    Refresh(TabsList);
                    Refresh(RecentList);
                    foreach (var list in _pinSections.Values) Refresh(list);
                }));
            });
        }

        private static void Refresh(System.Windows.Controls.ItemsControl ic)
        {
            try
            {
                var current = ic.ItemsSource;
                ic.ItemsSource = null;
                ic.ItemsSource = current;
            }
            catch { }
        }

        public void RefreshRecent()
        {
            if (_store == null) return;
            try
            {
                var n = _settings?.Load()?.Ui?.RecentItemsCount ?? 12;
                if (n <= 0) n = 12;
                var tabs = _store.ListTabs(null, null, "ts DESC");
                var rows = tabs.Take(n).Select(t => MakeRowVm(t)).ToList();
                RecentList.ItemsSource = rows;
                RecentEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                KickGitStatusUpdate(rows);
            }
            catch (Exception ex)
            {
                _log?.Error("RefreshRecent failed", ex);
            }
        }

        private static string SortToOrderBy(string sortMode)
        {
            switch (sortMode)
            {
                case "name-asc":    return "name COLLATE NOCASE ASC, ts DESC";
                case "name-desc":   return "name COLLATE NOCASE DESC, ts DESC";
                case "folder-name": return "folder COLLATE NOCASE ASC, name COLLATE NOCASE ASC";
                case "recent":
                default:            return "ts DESC";
            }
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TabsList.SelectedItem is TabRowVm t) _ = _openTabId?.Invoke(t.Source.TabId);
        }

        private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (RecentList.SelectedItem is TabRowVm t) _ = _openTabId?.Invoke(t.Source.TabId);
        }

        private void OnSelectionChanged(TabSummary t)
        {
            if (t == null) { _detailPane.Clear(); return; }
            _detailPane.Show(t);
        }

        // -------- context menu --------

        private TabSummary GetContextTarget(object sender)
        {
            var mi = sender as MenuItem;
            var cm = mi?.Parent as ContextMenu ?? FindAncestorContextMenu(mi);
            var target = cm?.PlacementTarget;
            if (target is ListBoxItem lbi)
            {
                if (lbi.DataContext is TabRowVm vm) return vm.Source;
                if (lbi.DataContext is SnippetRowVm svm) return svm.Source;
                if (lbi.DataContext is TabSummary t2) return t2;
            }
            if (TabsList.SelectedItem is TabRowVm vm2) return vm2.Source;
            if (RecentList.SelectedItem is TabRowVm vm3) return vm3.Source;
            return null;
        }

        private static ContextMenu FindAncestorContextMenu(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ContextMenu cm) return cm;
                d = LogicalTreeHelper.GetParent(d) ?? System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void OnCtx_Open(object sender, RoutedEventArgs e)
        {
            var t = GetContextTarget(sender); if (t == null) return;
            _ = _openTabId?.Invoke(t.TabId);
        }

        private void OnCtx_OpenAsNew(object sender, RoutedEventArgs e)
        {
            var t = GetContextTarget(sender); if (t == null) return;
            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new KeyValuePair<string, object>("$t", t.TabId) }, 1);
            if (snaps.Count > 0 && OpenSnapshotHandler != null) _ = OpenSnapshotHandler(snaps[0]);
        }

        private void OnCtx_CopyId(object sender, RoutedEventArgs e)
        {
            var t = GetContextTarget(sender); if (t == null) return;
            try { Clipboard.SetText(t.TabId ?? ""); } catch { }
        }

        private void OnCtx_CopyPath(object sender, RoutedEventArgs e)
        {
            var t = GetContextTarget(sender); if (t == null) return;
            var path = LookupOriginalFilePath(t);
            try { Clipboard.SetText(path ?? ""); } catch { }
        }

        private void OnCtx_RevealSnapshot(object sender, RoutedEventArgs e)
        {
            // Snapshots now live in the SQLite DB rather than on disk; "Reveal" opens the
            // storage root containing index.db so the user can see the canonical store.
            var path = _store?.Root;
            if (string.IsNullOrEmpty(path)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
        }

        // -------- snippets / source control --------

        public sealed class SnippetRowVm
        {
            public TabSummary Source { get; }
            public Git.GitFileStatus Status { get; }
            public string FilePath { get; }
            public SnippetRowVm(TabSummary src, Git.GitFileStatus st, string path)
            { Source = src; Status = st; FilePath = path; }

            public string FileName => string.IsNullOrEmpty(FilePath)
                ? (Source.Name ?? "(unnamed)")
                : System.IO.Path.GetFileName(FilePath);

            public string Letter
            {
                get
                {
                    switch (Status)
                    {
                        case Git.GitFileStatus.Modified:  return "M";
                        case Git.GitFileStatus.Untracked: return "U";
                        case Git.GitFileStatus.Staged:    return "A";
                        default: return " ";
                    }
                }
            }

            public string StatusName
            {
                get
                {
                    switch (Status)
                    {
                        case Git.GitFileStatus.Modified:  return "Modified";
                        case Git.GitFileStatus.Untracked: return "Untracked";
                        case Git.GitFileStatus.Staged:    return "Staged (added)";
                        default: return Status.ToString();
                    }
                }
            }

            public System.Windows.Media.Brush LetterBrush
            {
                get
                {
                    // Match VS Code source-control colour conventions.
                    switch (Status)
                    {
                        case Git.GitFileStatus.Modified:
                            return new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0xE2, 0xC0, 0x8D)); // amber
                        case Git.GitFileStatus.Untracked:
                            return new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0x73, 0xC9, 0x91)); // green
                        case Git.GitFileStatus.Staged:
                            return new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0x81, 0xB8, 0x8B)); // staged-green
                        default:
                            return System.Windows.Media.Brushes.Gray;
                    }
                }
            }
        }

        public void RefreshSnippets()
        {
            if (_store == null || _settings == null) return;
            var s = _settings.Load();
            var treeFolder = string.IsNullOrWhiteSpace(s.SavedScripts?.TreeFolder)
                ? "Saved Scripts" : s.SavedScripts.TreeFolder.Trim().Trim('/');

            List<TabSummary> tabs;
            try
            {
                tabs = _store.ListTabs("folder=$f OR folder LIKE $fp",
                    new[]
                    {
                        new KeyValuePair<string, object>("$f",  treeFolder),
                        new KeyValuePair<string, object>("$fp", treeFolder + "/%")
                    }, "ts DESC");
            }
            catch (Exception ex) { _log?.Error("RefreshSnippets list failed", ex); return; }

            // Resolve git status off the UI thread, then filter to uncommitted on the dispatcher.
            Task.Run(() =>
            {
                var paths = new List<string>();
                var pathByTab = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var t in tabs)
                {
                    var p = LookupOriginalFilePath(t);
                    if (!string.IsNullOrEmpty(p)) { paths.Add(p); pathByTab[t.TabId] = p; }
                }
                Dictionary<string, Git.GitFileStatus> map = null;
                if (paths.Count > 0)
                {
                    try { map = _gitResolver.Resolve(paths); } catch { }
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var rows = new List<SnippetRowVm>();
                    foreach (var t in tabs)
                    {
                        if (!pathByTab.TryGetValue(t.TabId, out var path)) continue;
                        var st = (map != null && map.TryGetValue(path, out var s2))
                            ? s2
                            : (_gitStatusByPath.TryGetValue(path, out var cached) ? cached : Git.GitFileStatus.Unknown);
                        if (map != null) _gitStatusByPath[path] = st;
                        if (st == Git.GitFileStatus.Modified
                            || st == Git.GitFileStatus.Untracked
                            || st == Git.GitFileStatus.Staged)
                        {
                            rows.Add(new SnippetRowVm(t, st, path));
                        }
                    }
                    SnippetsList.ItemsSource = rows;
                    SnippetsHeaderText.Text = rows.Count == 0
                        ? "SOURCE CONTROL — SNIPPETS"
                        : $"SOURCE CONTROL — SNIPPETS ({rows.Count})";
                    SnippetsEmptyHint.Visibility = rows.Count == 0
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                }));
            });
        }

        private void OnSnippetsDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SnippetsList.SelectedItem is SnippetRowVm vm) _ = _openTabId?.Invoke(vm.Source.TabId);
        }

        private void OnCommitMessage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Ctrl+Enter triggers Commit All — VS Code convention.
            if (e.Key == System.Windows.Input.Key.Enter
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                OnCommitAllSnippets_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void OnStageSnippet_Click(object sender, RoutedEventArgs e)
        {
            var vm = (sender as Button)?.Tag as SnippetRowVm;
            if (vm == null) return;
            if (string.IsNullOrEmpty(vm.FilePath) || !System.IO.File.Exists(vm.FilePath))
            {
                ShowInfo("File not found: " + vm.FilePath); return;
            }
            var r = GitHelper.Add(vm.FilePath, _log);
            if (!r.Ok)
            {
                ShowInfo("git add failed: " + (r.StdErr ?? r.Error ?? r.StdOut));
                return;
            }
            _gitResolver.Invalidate();
            RefreshSnippets();
        }

        private void OnCommitSnippet_Click(object sender, RoutedEventArgs e)
        {
            var vm = (sender as Button)?.Tag as SnippetRowVm;
            if (vm == null) return;
            if (string.IsNullOrEmpty(vm.FilePath) || !System.IO.File.Exists(vm.FilePath))
            {
                ShowInfo("File not found: " + vm.FilePath); return;
            }
            var msg = (CommitMessageBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(msg))
                msg = "Update " + (vm.Source.Name ?? System.IO.Path.GetFileNameWithoutExtension(vm.FilePath));

            var added = GitHelper.Add(vm.FilePath, _log);
            if (!added.Ok)
            {
                ShowInfo("git add failed: " + (added.StdErr ?? added.Error ?? added.StdOut));
                return;
            }
            var commit = GitHelper.Commit(vm.FilePath, msg, _log);
            if (!commit.Ok)
            {
                ShowInfo("git commit failed: " + (commit.StdErr ?? commit.Error ?? commit.StdOut));
                return;
            }
            _gitResolver.Invalidate();
            RefreshSnippets();
        }

        private void OnCommitAllSnippets_Click(object sender, RoutedEventArgs e)
        {
            var rows = (SnippetsList.ItemsSource as IEnumerable<SnippetRowVm>)?.ToList();
            if (rows == null || rows.Count == 0) return;

            var msg = (CommitMessageBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) msg = "Update snippets";

            // Group by repo so we do a single commit per repo containing all staged paths.
            var byRepo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.FilePath) || !System.IO.File.Exists(r.FilePath)) continue;
                var repo = GitHelper.FindRepoRoot(System.IO.Path.GetDirectoryName(r.FilePath));
                if (repo == null) continue;
                if (!byRepo.TryGetValue(repo, out var list)) { list = new List<string>(); byRepo[repo] = list; }
                list.Add(r.FilePath);
            }

            foreach (var pair in byRepo)
            {
                var repo = pair.Key;
                foreach (var p in pair.Value)
                {
                    var added = GitHelper.Add(p, _log);
                    if (!added.Ok)
                    {
                        ShowInfo("git add failed for " + p + ": " + (added.StdErr ?? added.Error));
                        return;
                    }
                }
                // Commit only the listed paths in this repo, with the user's message.
                var safe = msg.Replace("\"", "\\\"");
                var quoted = string.Join(" ", pair.Value.Select(p => "\"" + p + "\""));
                var result = GitHelper.Run(repo, $"commit -m \"{safe}\" -- {quoted}", _log);
                if (!result.Ok)
                {
                    ShowInfo("git commit failed in " + repo + ": " + (result.StdErr ?? result.Error ?? result.StdOut));
                    return;
                }
            }

            CommitMessageBox.Text = "";
            _gitResolver.Invalidate();
            RefreshSnippets();
        }

        // -------- save to scripts folder --------

        private void OnCtx_SaveToScripts(object sender, RoutedEventArgs e)
        {
            var t = GetContextTarget(sender);
            SaveTabToScriptsFolder(t);
        }

        private void OnSaveToScripts_Click(object sender, RoutedEventArgs e)
        {
            // Per-row button: the button's Tag is bound to the row's data context (TabRowVm).
            var btn = sender as Button;
            TabSummary t = null;
            if (btn?.Tag is TabRowVm vm) t = vm.Source;
            else if (btn?.Tag is TabSummary ts) t = ts;
            SaveTabToScriptsFolder(t);
        }

        // Pending state for the inline save prompt: captured when the user invokes the action,
        // consumed when they click Save in the panel at the bottom of the tool window.
        private TabSummary _pendingSaveTab;
        private string _pendingSaveContent;
        private string _pendingSaveFolderPath;
        private string _pendingSaveTreeFolder;
        private string _pendingSaveDefaultName;

        private void SaveTabToScriptsFolder(TabSummary t)
        {
            if (t == null) return;
            if (_store == null || _settings == null) return;

            var s = _settings.Load();
            var folderPath = s.SavedScripts?.FolderPath;
            var treeFolder = string.IsNullOrWhiteSpace(s.SavedScripts?.TreeFolder)
                ? "Saved Scripts" : s.SavedScripts.TreeFolder.Trim().Trim('/');

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                folderPath = System.IO.Path.Combine(docs, "AutoTabOrganiser", "Scripts");
                try
                {
                    _settings.Mutate(cfg =>
                    {
                        if (cfg.SavedScripts == null) cfg.SavedScripts = new SavedScriptsSettings();
                        cfg.SavedScripts.FolderPath = folderPath;
                    });
                    _log?.Info("Backfilled savedScripts.folderPath at use-site: " + folderPath);
                }
                catch (Exception ex) { _log?.Error("Persist savedScripts.folderPath failed", ex); }
            }
            try { System.IO.Directory.CreateDirectory(folderPath); }
            catch (Exception ex)
            {
                _log?.Error("Create scripts folder failed: " + folderPath, ex);
                ShowInfo("Could not create or access " + folderPath + ": " + ex.Message);
                return;
            }

            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new KeyValuePair<string, object>("$t", t.TabId) }, 1);
            if (snaps.Count == 0)
            {
                ShowInfo("No snapshot is available for this tab yet.");
                return;
            }
            var content = _store.ReadSnapshotContentById(snaps[0].Id) ?? string.Empty;

            var defaultName =
                PathSanitiser.Sanitise(t.Name)
                ?? PathSanitiser.FromFirstLine(content)
                ?? ("script-" + (t.TabId ?? ""));

            _pendingSaveTab = t;
            _pendingSaveContent = content;
            _pendingSaveFolderPath = folderPath;
            _pendingSaveTreeFolder = treeFolder;
            _pendingSaveDefaultName = defaultName;

            // Populate the subfolder dropdown with existing subfolders under this tree root.
            var existing = _store.ListTabs("folder LIKE $fp AND folder != $f",
                new[]
                {
                    new KeyValuePair<string, object>("$fp", treeFolder + "/%"),
                    new KeyValuePair<string, object>("$f",  treeFolder)
                }, "folder ASC");
            var subfolders = existing
                .Select(tab => tab.Folder.Substring(treeFolder.Length + 1))
                .Where(sub => !string.IsNullOrEmpty(sub))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(sub => sub, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SaveSubfolderBox.ItemsSource = subfolders;

            // Pre-fill the subfolder box if the current tab already lives in a subfolder.
            if (!string.IsNullOrEmpty(t.Folder) && t.Folder.StartsWith(treeFolder + "/", StringComparison.OrdinalIgnoreCase))
                SaveSubfolderBox.Text = t.Folder.Substring(treeFolder.Length + 1);
            else
                SaveSubfolderBox.Text = "";

            SaveNameBox.Text = defaultName;
            SavePromptHint.Text = "Will be written to: " + folderPath;
            SavePrompt.Visibility = Visibility.Visible;
            SaveNameBox.SelectAll();
            SaveNameBox.Focus();
        }

        private void OnSavePromptCancel_Click(object sender, RoutedEventArgs e)
        {
            ClearPendingSave();
        }

        private void OnSaveNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { ClearPendingSave(); e.Handled = true; }
        }

        private void ClearPendingSave()
        {
            SavePrompt.Visibility = Visibility.Collapsed;
            SaveSubfolderBox.Text = "";
            SaveSubfolderBox.ItemsSource = null;
            _pendingSaveTab = null;
            _pendingSaveContent = null;
            _pendingSaveFolderPath = null;
            _pendingSaveTreeFolder = null;
            _pendingSaveDefaultName = null;
        }

        private void OnSavePromptOk_Click(object sender, RoutedEventArgs e)
        {
            var t = _pendingSaveTab;
            if (t == null) { ClearPendingSave(); return; }

            var raw = (SaveNameBox.Text ?? "").Trim();
            if (raw.Length == 0) { SaveNameBox.Focus(); return; }
            var picked = PathSanitiser.Sanitise(raw) ?? _pendingSaveDefaultName;

            var folderPath = _pendingSaveFolderPath;
            var treeFolder = _pendingSaveTreeFolder;
            var content = _pendingSaveContent ?? string.Empty;

            // Resolve subfolder: sanitise each path segment and rejoin.
            var rawSub = (SaveSubfolderBox.Text ?? "").Trim().Trim('/', '\\');
            var sanitisedSub = "";
            if (!string.IsNullOrEmpty(rawSub))
            {
                var segments = rawSub.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(seg => PathSanitiser.Sanitise(seg))
                    .Where(seg => !string.IsNullOrEmpty(seg))
                    .ToList();
                sanitisedSub = string.Join("/", segments);
            }

            var subDirAbs = string.IsNullOrEmpty(sanitisedSub)
                ? folderPath
                : Path.Combine(folderPath, sanitisedSub.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(subDirAbs);

            var fileName = picked + ".sql";
            var targetPath = Path.Combine(subDirAbs, fileName);
            var folderForMeta = string.IsNullOrEmpty(sanitisedSub)
                ? treeFolder
                : treeFolder + "/" + sanitisedSub;

            // Inject/update @folder, @file and @id so the saved file round-trips with metadata.
            var newContent = MetadataWriter.SetFolder(content, folderForMeta);
            newContent = MetadataWriter.SetFile(newContent, fileName);
            if (!string.IsNullOrEmpty(t.TabId))
                newContent = MetadataWriter.SetId(newContent, t.TabId);

            try
            {
                System.IO.File.WriteAllText(targetPath, newContent, new System.Text.UTF8Encoding(false));
                _log?.Info($"Saved tab {t.TabId} to {targetPath}");
            }
            catch (Exception ex)
            {
                _log?.Error("Write to scripts folder failed: " + targetPath, ex);
                ShowInfo("Could not write file: " + ex.Message);
                return;
            }

            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var meta = MetadataParser.Parse(newContent);
                var record = new SnapshotRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TabId = t.TabId,
                    FilePath = targetPath,
                    Folder = folderForMeta,
                    Name = picked,
                    ContentHash = Hashing.Sha256Hex(newContent),
                    Reason = "saved",
                    Ts = now,
                    Tags = meta.Tags ?? new List<string>(),
                    Desc = meta.Description
                };
                _store.WriteSnapshot(record, newContent);
            }
            catch (Exception ex) { _log?.Error("Index saved-script snapshot failed", ex); }

            _gitResolver.Invalidate();
            ClearPendingSave();
            ShowInfo("Saved " + fileName);
            RefreshTabs();
        }

        // -------- info bar --------

        private DispatcherTimer _infoBarTimer;

        public void ShowInfo(string message)
        {
            if (string.IsNullOrEmpty(message)) { InfoBar.Visibility = Visibility.Collapsed; return; }
            InfoBarText.Text = message;
            InfoBar.Visibility = Visibility.Visible;
            if (_infoBarTimer == null)
            {
                _infoBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _infoBarTimer.Tick += (s, e) => { _infoBarTimer.Stop(); InfoBar.Visibility = Visibility.Collapsed; };
            }
            _infoBarTimer.Stop();
            _infoBarTimer.Start();
        }

        private void OnInfoBarClose_Click(object sender, RoutedEventArgs e)
        {
            _infoBarTimer?.Stop();
            InfoBar.Visibility = Visibility.Collapsed;
        }

        // -------- search Esc-to-clear --------

        private void OnSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        }

        private void OnSearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        // -------- git --------

        private string LookupOriginalFilePath(TabSummary t)
        {
            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new KeyValuePair<string, object>("$t", t.TabId) }, 1);
            return snaps.Count == 0 ? null : snaps[0].FilePath;
        }

        private void OnCtx_GitAdd(object sender, RoutedEventArgs e) => RunGit(sender, "add", path => GitHelper.Add(path, _log));
        private void OnCtx_GitCommitAuto(object sender, RoutedEventArgs e)
        {
            RunGit(sender, "commit", path =>
            {
                var added = GitHelper.Add(path, _log);
                if (!added.Ok) return added;
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                return GitHelper.Commit(path, $"Update {name}", _log);
            });
        }
        private void OnCtx_GitCommit(object sender, RoutedEventArgs e)
        {
            RunGit(sender, "commit", path =>
            {
                var msg = PromptForCommitMessage(System.IO.Path.GetFileName(path));
                if (msg == null) return new GitResult { Error = "cancelled" };
                var added = GitHelper.Add(path, _log);
                if (!added.Ok) return added;
                return GitHelper.Commit(path, msg, _log);
            });
        }
        private void OnCtx_GitStatus(object sender, RoutedEventArgs e) => RunGit(sender, "status", path => GitHelper.Status(path, _log));

        private void RunGit(object sender, string verb, Func<string, GitResult> op)
        {
            var t = GetContextTarget(sender); if (t == null) return;
            var path = LookupOriginalFilePath(t);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                ShowInfo("This tab has no file on disk yet (untitled). Save it first.");
                return;
            }
            var repo = GitHelper.FindRepoRoot(System.IO.Path.GetDirectoryName(path));
            if (repo == null)
            {
                ShowInfo("No git repository found containing " + path);
                return;
            }
            try
            {
                var r = op(path);
                if (!r.Ok && string.IsNullOrEmpty(r.Error))
                    ShowInfo($"git {verb} exited with code {r.ExitCode}: {(r.StdErr ?? r.StdOut)}");
            }
            catch (Exception ex) { _log?.Error("git command failed", ex); }
        }

        private static string PromptForCommitMessage(string fileName)
        {
            var win = new Window
            {
                Title = "git commit — " + fileName,
                Width = 460, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(System.Windows.Application.Current?.MainWindow ?? null)
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Commit message:", Margin = new Thickness(8) };
            Grid.SetRow(label, 0);
            var tb = new TextBox { Margin = new Thickness(8, 0, 8, 8), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = "Update " + System.IO.Path.GetFileNameWithoutExtension(fileName) };
            Grid.SetRow(tb, 1);
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            var ok = new Button { Content = "Commit", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);
            Grid.SetRow(btnPanel, 2);

            grid.Children.Add(label);
            grid.Children.Add(tb);
            grid.Children.Add(btnPanel);
            win.Content = grid;

            string result = null;
            ok.Click += (s, e) => { result = tb.Text; win.DialogResult = true; };
            return win.ShowDialog() == true ? result : null;
        }

        public Func<SnapshotRecord, Task> OpenSnapshotHandler { get; set; }

        // -------- view-models --------

        public sealed class TagChip
        {
            public string Text { get; }
            public System.Windows.Media.Brush Brush { get; }
            public System.Windows.Media.Brush Foreground { get; }
            public TagChip(string text, System.Windows.Media.Brush brush)
            {
                Text = text;
                Brush = brush;
                Foreground = ContrastingForeground(brush);
            }
            private static System.Windows.Media.Brush ContrastingForeground(System.Windows.Media.Brush b)
            {
                if (b is System.Windows.Media.SolidColorBrush sb)
                {
                    var c = sb.Color;
                    // Perceived luminance — pick black on light chips, white on dark.
                    var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    return lum > 0.6 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
                }
                return System.Windows.Media.Brushes.White;
            }
        }

        public sealed class TabRowVm
        {
            public TabSummary Source { get; }
            public Git.GitFileStatus GitStatus { get; set; }
            public IList<TagChip> Tags { get; }

            public TabRowVm(TabSummary s, IDictionary<string, string> tagColours)
            {
                Source = s;
                GitStatus = Git.GitFileStatus.Unknown;
                Tags = BuildChips(s.TagsCsv, tagColours);
            }

            private static IList<TagChip> BuildChips(string csv, IDictionary<string, string> overrides)
            {
                var list = new List<TagChip>();
                if (string.IsNullOrEmpty(csv)) return list;
                foreach (var raw in csv.Split(','))
                {
                    var t = (raw ?? "").Trim();
                    if (t.Length == 0) continue;
                    list.Add(new TagChip(t, AutoTabOrganiser.Editor.TagColourResolver.Resolve(t, overrides)));
                }
                return list;
            }

            public string Title => Source.Name ?? "(unnamed)";

            public string Subtitle
            {
                get
                {
                    var folder = string.IsNullOrEmpty(Source.Folder) ? "" : Source.Folder + "/";
                    return folder;
                }
            }

            public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
            public bool HasTags => Tags != null && Tags.Count > 0;

            public string TooltipText
            {
                get
                {
                    var name = Source.Name ?? "(unnamed)";
                    var folder = string.IsNullOrEmpty(Source.Folder) ? "" : "\nFolder: " + Source.Folder;
                    var id = string.IsNullOrEmpty(Source.TabId) ? "" : "\nId: " + Source.TabId;
                    var tags = string.IsNullOrEmpty(Source.TagsCsv) ? "" : "\nTags: " + Source.TagsCsv;
                    return name + folder + tags + id;
                }
            }

            public bool IsOpen  => Source.IsOpen;
            public bool IsDirty => Source.IsDirty;

            // String name of the KnownMonikers entry to show for the git column. The XAML
            // data template binds an ImageMonikerConverter to this string.
            public string GitMonikerName
            {
                get
                {
                    switch (GitStatus)
                    {
                        case Git.GitFileStatus.Modified:  return "PendingChanges";
                        case Git.GitFileStatus.Staged:    return "PendingAddNode";
                        case Git.GitFileStatus.Untracked: return "DocumentOutline";
                        case Git.GitFileStatus.Clean:     return "StatusOK";
                        case Git.GitFileStatus.NotInRepo: return null;
                        default:                          return null;
                    }
                }
            }

            public string GitTooltip
            {
                get
                {
                    switch (GitStatus)
                    {
                        case Git.GitFileStatus.Modified:  return "Modified";
                        case Git.GitFileStatus.Staged:    return "Staged";
                        case Git.GitFileStatus.Untracked: return "Untracked";
                        case Git.GitFileStatus.Clean:     return "Clean";
                        case Git.GitFileStatus.NotInRepo: return "Not in a git repo";
                        default:                          return "";
                    }
                }
            }

            // Backwards-compat helpers used by code paths that still build text-only rows.
            public string Display => Title;
            public string Marker  => "";

            public override string ToString() => Title;
        }
    }
}
