using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AutoTabOrganiser.Editor;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tree;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>
    /// Tool window view-model. Owns the observable state for the side panel and the commands
    /// that the XAML binds to. Talks to <see cref="SnapshotStore"/>, <see cref="SettingsStore"/>
    /// and <see cref="GitStatusResolver"/>; the view contributes only thin glue (modal dialogs
    /// for tag picker / commit message prompt, plus focus management on the inline save panel).
    /// </summary>
    internal sealed class ToolWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _disposed;

        /// <summary>
        /// Release the FileSystemWatcher and DispatcherTimer resources this VM owns. Safe to
        /// call multiple times. Important for multi-instance tool windows: when a secondary
        /// instance is closed, its VM is abandoned and would otherwise leak OS file-watch
        /// handles and keep firing Dispatcher work into a dead UI.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { DisposeStoredQueriesWatcher(); } catch { }
            try
            {
                foreach (var kv in _gitDirWatchers)
                {
                    try { kv.Value?.Dispose(); } catch { }
                }
                _gitDirWatchers.Clear();
            }
            catch { }
            try { _storedQueriesWatcherDebounce?.Stop(); } catch { }
            _storedQueriesWatcherDebounce = null;
            try { _lastSnapshotTimer?.Stop(); } catch { }
            _lastSnapshotTimer = null;
            try { _searchDebounce?.Stop(); } catch { }
            _searchDebounce = null;
            try { _infoBarTimer?.Stop(); } catch { }
            _infoBarTimer = null;
        }

        // ---- dependencies ----
        private readonly SnapshotStore _store;
        private readonly SettingsStore _settings;
        private readonly Logger _log;
        private readonly Func<string, Task> _openTabId;
        private readonly Action _openSettings;
        private readonly Action _snapshotNow;
        private readonly Action _quickSwitcher;
        private readonly Action _tagConfig;
        private readonly Action _newView;
        private readonly Func<SnapshotRecord, Task> _openAsNewSnapshot;
        private readonly Func<List<string>, List<string>, List<string>> _showTagPicker;
        private readonly Func<string, string> _promptCommitMessage;
        private readonly Action<string, string, GitFileStatus> _showGitDiff;
        private readonly Func<string, string, bool> _confirm;
        private readonly Action<string> _showInfo;
        private readonly Action _onSavePromptShown;
        private readonly GitStatusResolver _gitResolver = new GitStatusResolver();
        // ConcurrentDictionary because writers (FS-watcher OptimisticMarkModified, Task.Run
        // continuations in KickGitStatusUpdate / RefreshStoredQueries) and the dispatcher
        // reader (MakeRowVm) touch this without sharing a lock.
        private readonly ConcurrentDictionary<string, GitFileStatus> _gitStatusByPath =
            new ConcurrentDictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly Dispatcher _dispatcher;

        // Throttle for the duplicate sweep — Environment.TickCount when it last ran. Set to
        // int.MinValue so the very first refresh after launch still runs the sweep.
        private int _lastSweepTickMs = int.MinValue;
        // Single-flight gate for MaybeSweepDuplicates. CompareExchange flips 0→1 to claim the
        // run; any concurrent caller sees the existing 1 and bails. Reset to 0 in the finally.
        // Without this, two FS-watcher events firing within the throttle window can both enter
        // the body and run the (expensive) sweep concurrently — wasted I/O even though the
        // sweep itself is idempotent.
        private int _sweepInFlight; // 0 = idle, 1 = running

        // Last "Stored queries refresh: …" summary written to the log; identical consecutive
        // refreshes are not re-logged (the refresh runs on every snapshot write).
        private string _lastStoredQueriesRefreshLog;

        // ---- observable collections ----
        public ObservableCollection<TabRowViewModel> Tabs { get; } = new ObservableCollection<TabRowViewModel>();
        public ObservableCollection<TabRowViewModel> Recent { get; } = new ObservableCollection<TabRowViewModel>();
        public ObservableCollection<StoredQueryRowViewModel> StoredQueries { get; } = new ObservableCollection<StoredQueryRowViewModel>();
        public ObservableCollection<PinnedSectionViewModel> PinnedSections { get; } = new ObservableCollection<PinnedSectionViewModel>();
        public ObservableCollection<string> SaveSubfolderItems { get; } = new ObservableCollection<string>();
        // Chip strip at the top of the unified Recent/Search section: distinct tags drawn from
        // the user's last N opened tabs. Clicking a chip toggles `#tag` in SearchText.
        public ObservableCollection<TagFilterChip> RecentTagChips { get; } = new ObservableCollection<TagFilterChip>();
        public bool HasRecentTagChips => RecentTagChips.Count > 0;

        /// <summary>
        /// Items rendered in the unified list. When the search box is empty we show the
        /// unfiltered Recent list (default mode the user opens to); typing flips this to the
        /// search-filtered Tabs collection. Bind the ListBox's ItemsSource here so the
        /// switch is a single property-change rather than a swap of two visually-stacked
        /// list controls.
        /// </summary>
        public System.Collections.IEnumerable DisplayedItems
            => string.IsNullOrEmpty(_searchText) ? (System.Collections.IEnumerable)Recent : Tabs;

        public bool ListEmptyVisible
        {
            get
            {
                if (string.IsNullOrEmpty(_searchText)) return Recent.Count == 0;
                return Tabs.Count == 0;
            }
        }
        public string ListEmptyText
            => string.IsNullOrEmpty(_searchText)
                ? "No recent tabs yet — open a query."
                : "No tabs match. Try clearing the search.";

        // ---- search / sort ----
        private string _searchText = "";
        private DispatcherTimer _searchDebounce;
        public string SearchText
        {
            get => _searchText;
            set
            {
                var newValue = value ?? "";
                if (_searchText == newValue) return;
                var wasEmpty = string.IsNullOrEmpty(_searchText);
                var nowEmpty = string.IsNullOrEmpty(newValue);
                _searchText = newValue;
                Notify();
                Notify(nameof(ShowSearchClear));
                // Empty↔non-empty transitions swap which collection the unified list shows.
                if (wasEmpty != nowEmpty)
                {
                    Notify(nameof(DisplayedItems));
                    Notify(nameof(ListEmptyVisible));
                    Notify(nameof(ListEmptyText));
                }
                // Recompute chip highlight state regardless of transition — tags can be toggled
                // mid-typing and the chip's IsActive must mirror what the parser will see.
                RefreshTagChipActiveStates();
                DebounceSearch();
            }
        }
        public bool ShowSearchClear => !string.IsNullOrEmpty(_searchText);

        private string _sortMode = "recent";
        public string SortMode
        {
            get => _sortMode;
            set { if (_sortMode == value) return; _sortMode = value ?? "recent"; Notify(); RefreshTabs(); }
        }

        // ---- empty placeholders / counts ----
        private bool _tabsEmptyVisible;
        public bool TabsEmptyVisible { get => _tabsEmptyVisible; private set { if (_tabsEmptyVisible == value) return; _tabsEmptyVisible = value; Notify(); } }

        private bool _recentEmptyVisible;
        public bool RecentEmptyVisible { get => _recentEmptyVisible; private set { if (_recentEmptyVisible == value) return; _recentEmptyVisible = value; Notify(); } }

        private bool _storedQueriesEmptyVisible;
        public bool StoredQueriesEmptyVisible { get => _storedQueriesEmptyVisible; private set { if (_storedQueriesEmptyVisible == value) return; _storedQueriesEmptyVisible = value; Notify(); } }

        private string _storedQueriesHeader = "SOURCE CONTROL — STORED QUERIES";
        public string StoredQueriesHeader { get => _storedQueriesHeader; private set { if (_storedQueriesHeader == value) return; _storedQueriesHeader = value; Notify(); } }

        private string _commitMessage = "";
        public string CommitMessage
        {
            get => _commitMessage;
            set { if (_commitMessage == value) return; _commitMessage = value ?? ""; Notify(); }
        }

        // ---- storage path footer ----
        public string StoragePath { get; private set; }

        // ---- session restore banner ----
        private bool _sessionRestoreVisible;
        public bool SessionRestoreVisible
        {
            get => _sessionRestoreVisible;
            private set { if (_sessionRestoreVisible == value) return; _sessionRestoreVisible = value; Notify(); }
        }

        private string _sessionRestoreText = "";
        public string SessionRestoreText
        {
            get => _sessionRestoreText;
            private set { if (_sessionRestoreText == value) return; _sessionRestoreText = value; Notify(); }
        }

        private Action _sessionRestoreAction;

        /// <summary>
        /// Surface the "reopen last session" offer. Called once by the package after wiring;
        /// content survives in the snapshot store regardless, this is just the one-click path.
        /// </summary>
        public void OfferSessionRestore(int tabCount, Action reopen)
        {
            if (tabCount <= 0 || reopen == null) return;
            _sessionRestoreAction = reopen;
            SessionRestoreText = tabCount == 1
                ? "Last session had 1 open tab."
                : $"Last session had {tabCount} open tabs.";
            SessionRestoreVisible = true;
        }

        private void RestoreSession()
        {
            var action = _sessionRestoreAction;
            _sessionRestoreAction = null;
            SessionRestoreVisible = false;
            try { action?.Invoke(); }
            catch (Exception ex) { _log?.Error("Session restore failed", ex); Info("Session restore failed: " + ex.Message); }
        }

        private void DismissSessionRestore()
        {
            _sessionRestoreAction = null;
            SessionRestoreVisible = false;
        }

        // ---- info bar ----
        private string _infoMessage = "";
        private bool _infoBarVisible;
        private DispatcherTimer _infoBarTimer;
        public string InfoMessage { get => _infoMessage; private set { if (_infoMessage == value) return; _infoMessage = value; Notify(); } }
        public bool InfoBarVisible { get => _infoBarVisible; private set { if (_infoBarVisible == value) return; _infoBarVisible = value; Notify(); } }

        // ---- save-to-scripts inline panel ----
        private bool _savePromptVisible;
        public bool SavePromptVisible { get => _savePromptVisible; private set { if (_savePromptVisible == value) return; _savePromptVisible = value; Notify(); } }

        private string _saveSubfolder = "";
        public string SaveSubfolder { get => _saveSubfolder; set { if (_saveSubfolder == value) return; _saveSubfolder = value ?? ""; Notify(); } }

        private string _saveFileName = "";
        public string SaveFileName { get => _saveFileName; set { if (_saveFileName == value) return; _saveFileName = value ?? ""; Notify(); } }

        private string _savePromptHint = "";
        public string SavePromptHint { get => _savePromptHint; private set { if (_savePromptHint == value) return; _savePromptHint = value; Notify(); } }

        private TabSummary _pendingSaveTab;
        private string _pendingSaveContent;
        private string _pendingSaveFolderPath;
        private string _pendingSaveTreeFolder;
        private string _pendingSaveDefaultName;

        // ---- selection / detail ----
        private TabRowViewModel _selected;
        public TabRowViewModel Selected
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                Notify();
                SelectionChanged?.Invoke(this, _selected?.Source);
            }
        }
        public event EventHandler<TabSummary> SelectionChanged;

        // ---- active tab tracking ----

        private string _activeTabId;
        /// <summary>
        /// The tab_id of the SSMS document the user is currently focused on. Setter pins
        /// the matching row to the top of <see cref="Recent"/>; if the active tab isn't
        /// already in the recent list (truncated by item-count), it'll be picked up on
        /// the next <see cref="RefreshRecent"/>.
        /// </summary>
        public string ActiveTabId
        {
            get => _activeTabId;
            set
            {
                if (_activeTabId == value) return;
                _activeTabId = value;
                Notify();
                ReorderRecentForActive();
            }
        }

        private void ReorderRecentForActive()
        {
            if (string.IsNullOrEmpty(_activeTabId) || Recent.Count == 0) return;
            var existing = Recent.FirstOrDefault(r => r.Source != null && r.Source.TabId == _activeTabId);
            if (existing == null) return;
            var idx = Recent.IndexOf(existing);
            if (idx <= 0) return;
            Recent.RemoveAt(idx);
            Recent.Insert(0, existing);
        }

        // ---- commands ----
        public ICommand OpenStorageCommand { get; }
        public ICommand OpenConfigFolderCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand SnapshotNowCommand { get; }
        public ICommand QuickSwitcherCommand { get; }
        public ICommand TagConfigCommand { get; }
        public ICommand NewViewCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand CloseInfoBarCommand { get; }

        public ICommand OpenTabCommand { get; }
        public ICommand OpenAsNewCommand { get; }
        public ICommand CopyIdCommand { get; }
        public ICommand CopyPathCommand { get; }
        public ICommand RevealSnapshotCommand { get; }
        public ICommand DeleteTabCommand { get; }

        public ICommand SaveToScriptsCommand { get; }
        public ICommand ConfirmSaveScriptsCommand { get; }
        public ICommand CancelSaveScriptsCommand { get; }
        public ICommand BrowseSaveFolderCommand { get; }

        public ICommand StageStoredQueryCommand { get; }
        public ICommand CommitStoredQueryCommand { get; }
        public ICommand DiffStoredQueryCommand { get; }
        public ICommand CommitAllStoredQueriesCommand { get; }
        public ICommand OpenStoredQueriesFolderCommand { get; }
        public ICommand OpenStoredQueriesTerminalCommand { get; }

        public ICommand PinNewTagCommand { get; }
        public ICommand TogglePinTagCommand { get; }
        public ICommand UnpinTagCommand { get; }

        public ICommand GitAddCommand { get; }
        public ICommand GitCommitAutoCommand { get; }
        public ICommand GitCommitCommand { get; }
        public ICommand GitStatusCommand { get; }

        public ICommand RestoreSessionCommand { get; }
        public ICommand DismissSessionRestoreCommand { get; }
        public ICommand ExportArchiveCommand { get; }
        public ICommand PushStoredQueriesCommand { get; }

        // ---- ctor ----

        public ToolWindowViewModel(SnapshotStore store, SettingsStore settings, Logger log,
                                   Func<string, Task> openTabId,
                                   Action openSettings,
                                   Action snapshotNow,
                                   Action quickSwitcher,
                                   Action tagConfig,
                                   Action newView,
                                   Func<SnapshotRecord, Task> openAsNewSnapshot,
                                   Func<List<string>, List<string>, List<string>> showTagPicker,
                                   Func<string, string> promptCommitMessage,
                                   Action<string, string, GitFileStatus> showGitDiff,
                                   Func<string, string, bool> confirm,
                                   Action<string> showInfo,
                                   Action onSavePromptShown,
                                   Dispatcher dispatcher)
        {
            _store = store;
            _settings = settings;
            _log = log;
            _openTabId = openTabId;
            _openSettings = openSettings;
            _snapshotNow = snapshotNow;
            _quickSwitcher = quickSwitcher;
            _tagConfig = tagConfig;
            _newView = newView;
            _openAsNewSnapshot = openAsNewSnapshot;
            _showTagPicker = showTagPicker;
            _promptCommitMessage = promptCommitMessage;
            _showGitDiff = showGitDiff;
            _confirm = confirm;
            _showInfo = showInfo;
            _onSavePromptShown = onSavePromptShown;
            _dispatcher = dispatcher ?? Application.Current?.Dispatcher;

            StoragePath = _store?.Root ?? "(unknown)";

            OpenStorageCommand          = new RelayCommand(OpenStorage);
            OpenConfigFolderCommand     = new RelayCommand(OpenConfigFolder);
            OpenSettingsCommand         = new RelayCommand(() => _openSettings?.Invoke());
            SnapshotNowCommand   = new RelayCommand(() => _snapshotNow?.Invoke());
            QuickSwitcherCommand = new RelayCommand(() => _quickSwitcher?.Invoke());
            TagConfigCommand     = new RelayCommand(() => _tagConfig?.Invoke());
            NewViewCommand       = new RelayCommand(() => _newView?.Invoke());
            ClearSearchCommand   = new RelayCommand(() => SearchText = "");
            CloseInfoBarCommand  = new RelayCommand(HideInfoBar);

            OpenTabCommand        = new RelayCommand(p => OpenTab(p as TabRowViewModel));
            OpenAsNewCommand      = new RelayCommand(p => OpenAsNew(p as TabRowViewModel));
            CopyIdCommand         = new RelayCommand(p => Copy((p as TabRowViewModel)?.Source?.TabId));
            CopyPathCommand       = new RelayCommand(p => Copy(LookupOriginalFilePath((p as TabRowViewModel)?.Source)));
            RevealSnapshotCommand = new RelayCommand(RevealSnapshot);
            DeleteTabCommand      = new RelayCommand(p => DeleteTabRow(SourceOf(p)));

            SaveToScriptsCommand        = new RelayCommand(p => SaveTabToScriptsFolder((p as TabRowViewModel)?.Source));
            ConfirmSaveScriptsCommand   = new RelayCommand(ConfirmSaveScripts);
            CancelSaveScriptsCommand    = new RelayCommand(ClearPendingSave);
            BrowseSaveFolderCommand     = new RelayCommand(BrowseSaveFolder);

            StageStoredQueryCommand          = new RelayCommand(p => StageStoredQuery(p as StoredQueryRowViewModel));
            CommitStoredQueryCommand         = new RelayCommand(p => CommitStoredQuery(p as StoredQueryRowViewModel));
            DiffStoredQueryCommand           = new RelayCommand(p => DiffStoredQuery(p as StoredQueryRowViewModel));
            CommitAllStoredQueriesCommand    = new RelayCommand(CommitAllStoredQueries);
            OpenStoredQueriesFolderCommand   = new RelayCommand(OpenStoredQueriesFolder);
            OpenStoredQueriesTerminalCommand = new RelayCommand(OpenStoredQueriesTerminal);

            PinNewTagCommand     = new RelayCommand(PinNewTag);
            TogglePinTagCommand  = new RelayCommand(p => TogglePin(p as string));
            UnpinTagCommand      = new RelayCommand(p => Unpin(p as string));

            GitAddCommand        = new RelayCommand(p => RunGit((p as TabRowViewModel)?.Source, "add",    path => GitHelper.Add(path, _log)));
            GitCommitAutoCommand = new RelayCommand(p => RunGit((p as TabRowViewModel)?.Source, "commit", path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var msg = $"Update {name}";
                if (!ValidateCommitMessage(msg)) return new GitResult { ExitCode = 1, Error = "(silent)" };
                var added = GitHelper.Add(path, _log);
                if (!added.Ok) return added;
                return GitHelper.Commit(path, msg, _log);
            }));
            GitCommitCommand = new RelayCommand(p => RunGit((p as TabRowViewModel)?.Source, "commit", path =>
            {
                var msg = _promptCommitMessage?.Invoke(Path.GetFileName(path));
                if (msg == null) return new GitResult { ExitCode = 1, Error = "(silent)" };
                if (!ValidateCommitMessage(msg)) return new GitResult { ExitCode = 1, Error = "(silent)" };
                var added = GitHelper.Add(path, _log);
                if (!added.Ok) return added;
                return GitHelper.Commit(path, msg, _log);
            }));
            GitStatusCommand = new RelayCommand(p => RunGit((p as TabRowViewModel)?.Source, "status", path => GitHelper.Status(path, _log)));

            RestoreSessionCommand        = new RelayCommand(RestoreSession);
            DismissSessionRestoreCommand = new RelayCommand(DismissSessionRestore);
            ExportArchiveCommand         = new RelayCommand(ExportArchive);
            PushStoredQueriesCommand     = new RelayCommand(PushStoredQueries);
        }

        // ---- public API used by the view / package ----

        public void Initialise(string sortMode)
        {
            _sortMode = sortMode ?? "recent";
            Notify(nameof(SortMode));
            LoadPinnedTags();
            EnsureStoredQueriesWatcher();
            EnsureLastSnapshotTimer();
            RefreshAll();
        }

        // ---- Stored Queries / .git directory watchers ----

        private FileSystemWatcher _storedQueriesWatcher;
        private DispatcherTimer _storedQueriesWatcherDebounce;
        private string _storedQueriesWatchedPath;

        // Per-repo watchers on the .git directory. Catches external git operations
        // (terminal commits, stash, checkout) when the repo root is above the stored-
        // queries folder — the stored-queries FS watcher alone wouldn't see those.
        private readonly Dictionary<string, FileSystemWatcher> _gitDirWatchers
            = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Watch the stored-queries folder for create/change/delete events so the panel
        /// reflects editor edits and direct file modifications without waiting for the
        /// next snapshot poll. Debounces bursts of FS events.
        /// </summary>
        private void EnsureStoredQueriesWatcher()
        {
            try
            {
                var s = _settings?.Load();
                var path = s?.SavedScripts?.FolderPath;
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    DisposeStoredQueriesWatcher();
                    return;
                }
                if (string.Equals(_storedQueriesWatchedPath, path, StringComparison.OrdinalIgnoreCase)
                    && _storedQueriesWatcher != null) return;

                DisposeStoredQueriesWatcher();
                _storedQueriesWatchedPath = path;
                _storedQueriesWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };
                // FS-watcher events fire on a threadpool thread; an exception escaping the
                // handler takes down the whole SSMS process. Nothing a refresh does is worth
                // that, so the handlers are hard guards.
                FileSystemEventHandler onChange = (sender, e) =>
                {
                    try
                    {
                        OptimisticMarkModified(e?.FullPath);
                        DebounceStoredQueriesRefresh();
                    }
                    catch (Exception ex) { _log?.Warn("Stored-queries watcher callback failed: " + ex.Message); }
                };
                _storedQueriesWatcher.Changed  += onChange;
                _storedQueriesWatcher.Created  += onChange;
                _storedQueriesWatcher.Deleted  += onChange;
                _storedQueriesWatcher.Renamed  += (s2, e2) =>
                {
                    try
                    {
                        OptimisticMarkModified(e2?.FullPath);
                        DebounceStoredQueriesRefresh();
                    }
                    catch (Exception ex) { _log?.Warn("Stored-queries watcher rename callback failed: " + ex.Message); }
                };
                // Buffer-overflow (too many FS events): the watcher drops events and raises
                // Error. A full refresh re-reads disk truth, so recovering is just refreshing.
                _storedQueriesWatcher.Error += (s2, e2) =>
                {
                    try { DebounceStoredQueriesRefresh(); } catch { }
                };
            }
            catch (Exception ex) { _log?.Warn("Stored-queries watcher init failed: " + ex.Message); }
        }

        private void DisposeStoredQueriesWatcher()
        {
            try { _storedQueriesWatcher?.Dispose(); } catch { }
            _storedQueriesWatcher = null;
            _storedQueriesWatchedPath = null;
        }

        /// <summary>
        /// Maintain a <c>.git</c> watcher for each repo containing one of the supplied
        /// stored-queries file paths, removing watchers for repos that no longer have any
        /// known files. Called after each <see cref="RefreshStoredQueries"/> with the
        /// fresh path set.
        /// </summary>
        private void EnsureGitDirWatchers(IEnumerable<string> filePaths)
        {
            var repoRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in filePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    var repo = GitHelper.FindRepoRoot(Path.GetDirectoryName(p));
                    if (!string.IsNullOrEmpty(repo)) repoRoots.Add(repo);
                }
                catch { }
            }

            foreach (var repo in repoRoots)
            {
                if (_gitDirWatchers.ContainsKey(repo)) continue;
                var gitDir = Path.Combine(repo, ".git");
                if (!Directory.Exists(gitDir)) continue;
                try
                {
                    var w = new FileSystemWatcher(gitDir)
                    {
                        // index, HEAD, refs/* — anything in .git changing is a strong signal
                        // git state moved. Subdirectories on so we catch refs/heads/*.
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        EnableRaisingEvents = true
                    };
                    // Same hard guard as the stored-queries watcher: these fire on a threadpool
                    // thread and an escaping exception would crash SSMS.
                    //
                    // *.lock files are transient git-internal artifacts (index.lock, ref locks)
                    // created even by read-only commands; reacting to them re-triggers the very
                    // refresh that ran git in the first place. Real state changes always also
                    // touch a durable file (index, HEAD, refs/*), so skipping locks loses nothing.
                    FileSystemEventHandler onGitChange = (s, e) =>
                    {
                        try
                        {
                            if (IsGitLockArtifact(e?.Name)) return;
                            DebounceStoredQueriesRefresh();
                        }
                        catch { }
                    };
                    w.Changed += onGitChange;
                    w.Created += onGitChange;
                    w.Deleted += onGitChange;
                    w.Renamed += (s, e) =>
                    {
                        try
                        {
                            if (IsGitLockArtifact(e?.Name) && IsGitLockArtifact(e?.OldName)) return;
                            DebounceStoredQueriesRefresh();
                        }
                        catch { }
                    };
                    _gitDirWatchers[repo] = w;
                }
                catch (Exception ex) { _log?.Debug("git-dir watcher init failed for " + repo + ": " + ex.Message); }
            }

            var stale = _gitDirWatchers.Keys.Where(k => !repoRoots.Contains(k)).ToList();
            foreach (var k in stale)
            {
                try { _gitDirWatchers[k].Dispose(); } catch { }
                _gitDirWatchers.Remove(k);
            }
        }

        /// <summary>True for transient git lock files (index.lock, refs/heads/main.lock, …).</summary>
        private static bool IsGitLockArtifact(string relativeName)
            => relativeName != null && relativeName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

        private void DebounceStoredQueriesRefresh()
        {
            Marshal(() =>
            {
                if (_storedQueriesWatcherDebounce == null)
                {
                    // 100ms — long enough to coalesce a burst of FS events from a single
                    // save, short enough to feel instant.
                    _storedQueriesWatcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    _storedQueriesWatcherDebounce.Tick += (s, e) =>
                    {
                        _storedQueriesWatcherDebounce.Stop();
                        _gitResolver.Invalidate();
                        RefreshStoredQueries();
                    };
                }
                _storedQueriesWatcherDebounce.Stop();
                _storedQueriesWatcherDebounce.Start();
            });
        }

        /// <summary>
        /// Optimistically promote the row matching <paramref name="changedPath"/> from Clean
        /// to Modified the instant the FS watcher fires, before waiting for git status.
        /// If git status later disagrees, the next refresh corrects the row.
        /// </summary>
        private void OptimisticMarkModified(string changedPath)
        {
            if (string.IsNullOrEmpty(changedPath)) return;
            // Replacement happens during the debounced RefreshStoredQueries; here we just
            // bump the in-memory cache so MakeRowVm picks up "Modified" if git is slow.
            _gitStatusByPath[changedPath] = GitFileStatus.Modified;
        }

        /// <summary>
        /// Apply an immediate UI update for a known-good post-git-op state. Pure in-memory
        /// work — no SQLite queries, no Task.Run, no collection replacement (only direct
        /// mutation or removal of the affected row). This is critical: previous versions
        /// did a SQLite query per visible row inside <c>UpdateGitStatusOn</c>'s
        /// <c>LookupOriginalFilePath</c>, blocking the UI thread for hundreds of milliseconds
        /// per click and causing subsequent clicks to be dropped.
        /// </summary>
        private void OptimisticSetGitStatus(string filePath, string tabId, GitFileStatus newStatus)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            _gitStatusByPath[filePath] = newStatus;

            // StoredQueries: mutate the matching row's Status in place (INPC re-renders the
            // cells), or remove it from the collection when the new status is Clean.
            for (int i = StoredQueries.Count - 1; i >= 0; i--)
            {
                var row = StoredQueries[i];
                if (!string.Equals(row.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (newStatus == GitFileStatus.Clean)
                    StoredQueries.RemoveAt(i);
                else
                    row.Status = newStatus;
            }
            StoredQueriesHeader = StoredQueries.Count == 0
                ? "SOURCE CONTROL — STORED QUERIES"
                : $"SOURCE CONTROL — STORED QUERIES ({StoredQueries.Count})";
            StoredQueriesEmptyVisible = StoredQueries.Count == 0;

            // Cross-section icon update by tabId match — zero I/O. No LookupOriginalFilePath
            // (which was the SQL hot path), no path comparison; we already know the tabId
            // for the row we just operated on.
            if (!string.IsNullOrEmpty(tabId))
            {
                MutateGitStatusByTabId(Tabs, tabId, newStatus);
                MutateGitStatusByTabId(Recent, tabId, newStatus);
                foreach (var section in PinnedSections)
                    MutateGitStatusByTabId(section.Items, tabId, newStatus);
            }
        }

        private static void MutateGitStatusByTabId(IEnumerable<TabRowViewModel> rows, string tabId, GitFileStatus status)
        {
            foreach (var vm in rows)
            {
                if (vm.Source != null && vm.Source.TabId == tabId)
                    vm.GitStatus = status;
            }
        }

        /// <summary>Called by the package after each tab update; keeps lists in sync.</summary>
        public void RefreshAll()
        {
            RefreshTabs();
            RefreshRecent();
            RefreshStoredQueries();
            RefreshAllPinnedSections();
        }

        // ---- search / list refresh ----

        private void DebounceSearch()
        {
            if (_searchDebounce == null)
            {
                _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); RefreshTabs(); };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        public void RefreshTabs()
        {
            if (_store == null) return;
            try
            {
                var query = SearchQueryParser.Parse(_searchText ?? "");
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Bare typed words also match against full content (FTS5) — same as the popup.
                var (where, pars) = SearchQueryParser.ToSql(
                    query, nowMs,
                    includeContentInDefault: true,
                    ftsAvailable: _store.FtsAvailable);
                var orderBy = SortToOrderBy(_sortMode);
                var tabs = _store.ListTabs(where, pars, orderBy);
                var rows = tabs.Select(MakeRowVm).ToList();
                ReplaceCollection(Tabs, rows);
                TabsEmptyVisible = rows.Count == 0;
                Notify(nameof(ListEmptyVisible));
                KickGitStatusUpdate(rows);
                // RefreshRecent intentionally NOT called here. Recent is always the unfiltered
                // top-N regardless of search text, so search keystrokes shouldn't trigger a
                // second ListTabs roundtrip. RefreshAll (called on snapshot writes / tab events)
                // still updates both.
            }
            catch (Exception ex) { _log?.Error("RefreshTabs failed", ex); }
        }

        public void RefreshRecent()
        {
            if (_store == null) return;
            try
            {
                var n = _settings?.Load()?.Ui?.RecentItemsCount ?? 12;
                if (n <= 0) n = 12;
                var tabs = _store.ListTabs(null, null, "ts DESC");
                RefreshRecentTagChips(tabs);
                var rows = tabs.Take(n).Select(MakeRowVm).ToList();

                // Ensure the active tab is in the list even if truncated, so the user's
                // current SSMS tab is always the first row.
                if (!string.IsNullOrEmpty(_activeTabId)
                    && !rows.Any(r => r.Source != null && r.Source.TabId == _activeTabId))
                {
                    var activeTab = tabs.FirstOrDefault(t => t.TabId == _activeTabId);
                    if (activeTab != null) rows.Insert(0, MakeRowVm(activeTab));
                }

                ReplaceCollection(Recent, rows);
                ReorderRecentForActive();
                RecentEmptyVisible = rows.Count == 0;
                Notify(nameof(ListEmptyVisible));
                KickGitStatusUpdate(rows);
                UpdateLastSnapshotFromRecent();
            }
            catch (Exception ex) { _log?.Error("RefreshRecent failed", ex); }
        }

        // ---- recent-tag chip strip ----

        /// <summary>How many of the most-recent tabs to scan when building the chip strip.</summary>
        private const int RecentTagChipScanWindow = 50;
        /// <summary>Cap on the number of chips actually rendered, to keep the strip compact.</summary>
        private const int RecentTagChipMaxCount = 20;

        /// <summary>
        /// Recompute <see cref="RecentTagChips"/> from the last N opened tabs (most-recent
        /// first). Distinct, ordered by first appearance so the freshest activity surfaces
        /// at the left. <paramref name="recentTabsDescByTs"/> is the same query result
        /// <see cref="RefreshRecent"/> already loaded — passing it through avoids a second
        /// round-trip to the store.
        /// </summary>
        private void RefreshRecentTagChips(IList<TabSummary> recentTabsDescByTs)
        {
            try
            {
                var overrides = _settings?.Load()?.Ui?.TagColours;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ordered = new List<string>();
                int scanned = 0;
                foreach (var t in recentTabsDescByTs)
                {
                    if (scanned++ >= RecentTagChipScanWindow) break;
                    if (string.IsNullOrEmpty(t?.TagsCsv)) continue;
                    foreach (var raw in t.TagsCsv.Split(','))
                    {
                        var tag = (raw ?? "").Trim();
                        if (tag.Length == 0) continue;
                        if (!seen.Add(tag)) continue;
                        ordered.Add(tag);
                        if (ordered.Count >= RecentTagChipMaxCount) goto done;
                    }
                }
                done:

                var newChips = ordered
                    .Select(tag => new TagFilterChip(
                        tag,
                        TagColourResolver.Resolve(tag, overrides),
                        new RelayCommand(() => ToggleTagInSearchText(tag))))
                    .ToList();

                RecentTagChips.Clear();
                foreach (var c in newChips) RecentTagChips.Add(c);
                RefreshTagChipActiveStates();
                Notify(nameof(HasRecentTagChips));
            }
            catch (Exception ex) { _log?.Debug("RefreshRecentTagChips failed: " + ex.Message); }
        }

        /// <summary>
        /// Mark each chip as active/inactive based on whether its tag is currently a
        /// (non-negated) `#tag` token in <see cref="SearchText"/>. Cheap — no DB roundtrip,
        /// safe to call on every keystroke from the SearchText setter.
        /// </summary>
        private void RefreshTagChipActiveStates()
        {
            var active = ExtractTagTokens(_searchText);
            foreach (var chip in RecentTagChips)
                chip.IsActive = active.Contains(chip.Text);
        }

        /// <summary>
        /// Pull the active `#tag` tokens out of a SearchText string using the same
        /// whitespace/quoting rules SearchQueryParser uses. Negated tokens (`-#tag`) are
        /// intentionally NOT included — those represent an explicit exclusion and don't
        /// imply the chip is "applied".
        /// </summary>
        private static HashSet<string> ExtractTagTokens(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return set;
            // Lightweight re-tokenise: same rules as SearchQueryParser.Tokenise (whitespace
            // with " " quoting). Duplicating the tiny loop here keeps this VM free of a
            // dependency on the parser's internal tokeniser.
            var sb = new StringBuilder();
            bool inQuote = false;
            void Flush()
            {
                if (sb.Length == 0) return;
                var token = sb.ToString();
                sb.Clear();
                if (token.Length > 1 && token[0] == '#') set.Add(token.Substring(1));
            }
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (!inQuote && char.IsWhiteSpace(c)) { Flush(); }
                else sb.Append(c);
            }
            Flush();
            return set;
        }

        /// <summary>
        /// Add or remove `#tag` from <see cref="SearchText"/>. Whitespace-aware so any
        /// other typed terms are preserved. Tag matching is case-insensitive; if the user
        /// has typed "#Mytag" and clicks the "mytag" chip, the existing token is removed.
        /// </summary>
        private void ToggleTagInSearchText(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            var current = _searchText ?? "";
            var rebuilt = new StringBuilder();
            bool removed = false;
            var sb = new StringBuilder();
            bool inQuote = false;
            void Flush()
            {
                if (sb.Length == 0) return;
                var token = sb.ToString();
                sb.Clear();
                if (token.Length > 1 && token[0] == '#'
                    && string.Equals(token.Substring(1), tag, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    return; // drop the token, swallow the leading separator below
                }
                if (rebuilt.Length > 0) rebuilt.Append(' ');
                rebuilt.Append(token);
            }
            for (int i = 0; i < current.Length; i++)
            {
                var c = current[i];
                if (c == '"') { sb.Append(c); inQuote = !inQuote; continue; }
                if (!inQuote && char.IsWhiteSpace(c)) { Flush(); }
                else sb.Append(c);
            }
            Flush();

            if (!removed)
            {
                if (rebuilt.Length > 0) rebuilt.Append(' ');
                rebuilt.Append('#').Append(tag);
            }
            // Setter handles Notify + chip-state recompute + debounced re-query.
            SearchText = rebuilt.ToString();
        }

        public void RefreshStoredQueries()
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
            catch (Exception ex) { _log?.Error("RefreshStoredQueries list failed", ex); return; }

            Task.Run(() =>
            {
                var paths = new List<string>();
                var pathByTab = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var t in tabs)
                {
                    var p = LookupOriginalFilePath(t);
                    if (!string.IsNullOrEmpty(p)) { paths.Add(p); pathByTab[t.TabId] = p; }
                }

                MaybeSweepDuplicates(tabs, pathByTab, s);

                Dictionary<string, GitFileStatus> map = null;
                if (paths.Count > 0)
                {
                    try { map = _gitResolver.Resolve(paths); }
                    catch (Exception ex) { _log?.Warn("git status resolve failed: " + ex.Message); }
                }

                // Diagnostic counts so an empty section is debuggable from the log.
                int modified = 0, untracked = 0, staged = 0, clean = 0, notInRepo = 0, unknown = 0;
                if (map != null)
                {
                    foreach (var kv in map)
                    {
                        switch (kv.Value)
                        {
                            case GitFileStatus.Modified:  modified++;  break;
                            case GitFileStatus.Untracked: untracked++; break;
                            case GitFileStatus.Staged:    staged++;    break;
                            case GitFileStatus.Clean:     clean++;     break;
                            case GitFileStatus.NotInRepo: notInRepo++; break;
                            default:                      unknown++;   break;
                        }
                    }
                }
                // Only log when the outcome changed — this runs on every snapshot write and
                // used to repeat an identical line thousands of times per day.
                var refreshSummary =
                    $"Stored queries refresh: folder='{treeFolder}', tabs={tabs.Count}, with-path={paths.Count}, " +
                    $"git: modified={modified}, untracked={untracked}, staged={staged}, clean={clean}, notInRepo={notInRepo}, unknown={unknown}";
                if (!string.Equals(refreshSummary, _lastStoredQueriesRefreshLog, StringComparison.Ordinal))
                {
                    _lastStoredQueriesRefreshLog = refreshSummary;
                    _log?.Info(refreshSummary);
                }

                // Branch + ahead/behind for the stored-queries repo — read-only local git
                // commands (rev-parse / rev-list), no network. log:null keeps the per-refresh
                // log noise down; failures just blank the indicator.
                string branchText = "";
                try
                {
                    var repoRoot = GitHelper.FindRepoRoot(ResolveStoredQueriesPath());
                    if (repoRoot != null)
                    {
                        var (branch, ahead, behind) = GitHelper.BranchStatus(repoRoot);
                        if (!string.IsNullOrEmpty(branch))
                        {
                            branchText = branch;
                            if (ahead > 0)  branchText += $" ↑{ahead}";
                            if (behind > 0) branchText += $" ↓{behind}";
                        }
                    }
                }
                catch (Exception ex) { _log?.Debug("Branch status failed: " + ex.Message); }

                Marshal(() =>
                {
                    StoredQueriesBranchText = branchText;
                    var desired = new List<(TabSummary t, GitFileStatus st, string path)>();
                    foreach (var t in tabs)
                    {
                        if (!pathByTab.TryGetValue(t.TabId, out var path)) continue;
                        var st = (map != null && map.TryGetValue(path, out var s2))
                            ? s2
                            : (_gitStatusByPath.TryGetValue(path, out var cached) ? cached : GitFileStatus.Unknown);
                        if (map != null) _gitStatusByPath[path] = st;
                        if (st == GitFileStatus.Modified
                            || st == GitFileStatus.Untracked
                            || st == GitFileStatus.Staged)
                        {
                            desired.Add((t, st, path));
                        }
                    }

                    ApplyStoredQueriesDiff(desired);
                    StoredQueriesHeader = StoredQueries.Count == 0
                        ? "SOURCE CONTROL — STORED QUERIES"
                        : $"SOURCE CONTROL — STORED QUERIES ({StoredQueries.Count})";
                    StoredQueriesEmptyVisible = StoredQueries.Count == 0;

                    // Keep .git watchers in sync with the current set of stored-query
                    // files so external git ops trigger refreshes without polling.
                    EnsureGitDirWatchers(paths);
                });
            });
        }

        /// <summary>
        /// Throttled duplicate sweep — runs at most once per
        /// <see cref="SnapshottingSettings.SweepDuplicatesIntervalSeconds"/>. Off when the
        /// <see cref="SnapshottingSettings.AutoSweepDuplicates"/> setting is false.
        /// Runs on the same background thread as the calling git-resolve work.
        /// </summary>
        private void MaybeSweepDuplicates(List<TabSummary> tabs, Dictionary<string, string> pathByTab, AppSettings s)
        {
            if (s?.Snapshotting == null || !s.Snapshotting.AutoSweepDuplicates) return;

            var intervalMs = Math.Max(1000, s.Snapshotting.SweepDuplicatesIntervalSeconds * 1000);
            var nowMs = Environment.TickCount;
            // unchecked subtract handles tick-count wraparound after ~25 days of uptime.
            if (_lastSweepTickMs != int.MinValue && unchecked(nowMs - _lastSweepTickMs) < intervalMs) return;

            // Single-flight: claim the run before recording the time. If a concurrent caller
            // beat us to it, bail without updating _lastSweepTickMs so we'll re-evaluate on
            // the next call instead of skipping the next throttle window entirely.
            if (System.Threading.Interlocked.CompareExchange(ref _sweepInFlight, 1, 0) != 0) return;
            _lastSweepTickMs = nowMs;

            try
            {
                if (pathByTab != null && pathByTab.Count > 0)
                {
                    var openByTabId = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var t in tabs) if (t.IsOpen && !string.IsNullOrEmpty(t.TabId)) openByTabId.Add(t.TabId);

                    var candidates = new List<StoredQueryDuplicateSweeper.Candidate>(pathByTab.Count);
                    foreach (var kv in pathByTab)
                    {
                        candidates.Add(new StoredQueryDuplicateSweeper.Candidate
                        {
                            TabId = kv.Key,
                            FilePath = kv.Value,
                        });
                    }

                    var fileSweeper = new StoredQueryDuplicateSweeper(_log);
                    // Deletion boundary: the sweep may only ever remove files under the
                    // configured stored-queries folder, no matter what paths the index holds.
                    var fileResult = fileSweeper.Sweep(candidates, tabId => openByTabId.Contains(tabId),
                                                       s?.SavedScripts?.FolderPath);
                    if (fileResult.DuplicatesDeleted > 0)
                    {
                        // Drop sweeped paths from pathByTab so the git-resolve below doesn't
                        // run on files we just deleted.
                        var deleted = new HashSet<string>(fileResult.DeletedPaths, StringComparer.OrdinalIgnoreCase);
                        var keysToRemove = pathByTab
                            .Where(kv => deleted.Contains(kv.Value))
                            .Select(kv => kv.Key)
                            .ToList();
                        foreach (var k in keysToRemove) pathByTab.Remove(k);
                    }
                }

                if (_store != null)
                {
                    // Collapse index rows whose canonical content is identical (copies that
                    // differ only by their @id line) — the file sweep above removes the
                    // duplicate .sql, but without this the loser's tabs_latest row lives on
                    // and the quick switcher keeps listing both.
                    try { _store.MergeDuplicateTabRows(); }
                    catch (Exception ex) { _log?.Warn("duplicate tab-row merge failed: " + ex.Message); }

                    try { _store.SweepCrossTabContentDuplicates(); }
                    catch (Exception ex) { _log?.Warn("cross-tab dedup failed: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("duplicate sweep failed: " + ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _sweepInFlight, 0);
            }
        }

        /// <summary>
        /// Reconcile <see cref="StoredQueries"/> with <paramref name="desired"/> using minimal
        /// mutations: rows whose tab is still wanted have their <see cref="StoredQueryRowViewModel.Status"/>
        /// updated in place (INPC re-renders the cells); orphan rows are removed; new tabs are
        /// inserted; out-of-order rows are moved.
        ///
        /// Why bother: never replace a row instance whose tab is still on the list. A
        /// <c>Clear()</c>+<c>Add()</c> pass fires CollectionChanged.Reset which makes the
        /// ListBox recreate every ListBoxItem — during that recreation, button hit-testing
        /// briefly fails and rapid clicks on adjacent (or the same) row get dropped. That's
        /// the same hazard <see cref="OptimisticSetGitStatus"/> avoids; this method makes the
        /// .git-watcher-driven refresh that fires ~100ms after each git op equally safe.
        /// </summary>
        private void ApplyStoredQueriesDiff(List<(TabSummary t, GitFileStatus st, string path)> desired)
        {
            // Key by TabId, not FilePath: two tabs can map to the same on-disk file (same .sql
            // opened in multiple SSMS windows, shared saved-snapshot path), so paths aren't
            // unique in `desired`. Keying by path collapses duplicates and would crash Move()
            // when two desired entries point at the same row.
            string TabIdOf(StoredQueryRowViewModel r) => r?.Source?.TabId ?? "";

            // Defensive uniqueness: the upstream contract is that `desired` already has one
            // entry per TabId, but if a caller bug ever produces a duplicate TabId the diff
            // loop below would call Move() on a single-element collection (oldIndex=0 → i=1)
            // and throw ArgumentOutOfRangeException. Collapse to first-occurrence-wins here
            // and warn so the upstream bug doesn't silently cascade into a UI crash.
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var deduped = new List<(TabSummary t, GitFileStatus st, string path)>(desired.Count);
                int dropped = 0;
                foreach (var d in desired)
                {
                    var tid = d.t?.TabId ?? "";
                    if (string.IsNullOrEmpty(tid)) { deduped.Add(d); continue; }
                    if (seen.Add(tid)) deduped.Add(d);
                    else dropped++;
                }
                if (dropped > 0)
                {
                    _log?.Warn($"ApplyStoredQueriesDiff: dropped {dropped} duplicate-tab-id entries from desired list.");
                    desired = deduped;
                }
            }

            var desiredTabIds = new HashSet<string>(
                desired.Select(d => d.t?.TabId ?? ""), StringComparer.Ordinal);

            for (int i = StoredQueries.Count - 1; i >= 0; i--)
            {
                if (!desiredTabIds.Contains(TabIdOf(StoredQueries[i])))
                    StoredQueries.RemoveAt(i);
            }

            var byTabId = new Dictionary<string, StoredQueryRowViewModel>(StringComparer.Ordinal);
            foreach (var row in StoredQueries)
            {
                var tid = TabIdOf(row);
                if (!string.IsNullOrEmpty(tid)) byTabId[tid] = row;
            }

            for (int i = 0; i < desired.Count; i++)
            {
                var d = desired[i];
                var tid = d.t?.TabId ?? "";

                if (i < StoredQueries.Count
                    && string.Equals(TabIdOf(StoredQueries[i]), tid, StringComparison.Ordinal))
                {
                    StoredQueries[i].Status = d.st;
                    continue;
                }
                if (!string.IsNullOrEmpty(tid) && byTabId.TryGetValue(tid, out var existing))
                {
                    var oldIndex = StoredQueries.IndexOf(existing);
                    if (oldIndex >= 0 && oldIndex != i) StoredQueries.Move(oldIndex, i);
                    existing.Status = d.st;
                }
                else
                {
                    var row = new StoredQueryRowViewModel(d.t, d.st, d.path);
                    StoredQueries.Insert(i, row);
                    if (!string.IsNullOrEmpty(tid)) byTabId[tid] = row;
                }
            }
        }

        private TabRowViewModel MakeRowVm(TabSummary t)
        {
            var overrides = _settings?.Load()?.Ui?.TagColours;
            var vm = new TabRowViewModel(t, overrides);
            var path = LookupOriginalFilePath(t);
            if (!string.IsNullOrEmpty(path) && _gitStatusByPath.TryGetValue(path, out var st)) vm.GitStatus = st;
            return vm;
        }

        private void KickGitStatusUpdate(IEnumerable<TabRowViewModel> rowVms)
        {
            var paths = new List<string>();
            foreach (var vm in rowVms)
            {
                var p = LookupOriginalFilePath(vm.Source);
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            if (paths.Count == 0) return;

            Task.Run(() =>
            {
                Dictionary<string, GitFileStatus> map;
                try { map = _gitResolver.Resolve(paths); } catch { return; }
                Marshal(() =>
                {
                    foreach (var kv in map) _gitStatusByPath[kv.Key] = kv.Value;
                    UpdateGitStatusOn(Tabs);
                    UpdateGitStatusOn(Recent);
                    foreach (var section in PinnedSections) UpdateGitStatusOn(section.Items);
                });
            });
        }

        private void UpdateGitStatusOn(IEnumerable<TabRowViewModel> rows)
        {
            foreach (var vm in rows)
            {
                var p = LookupOriginalFilePath(vm.Source);
                if (!string.IsNullOrEmpty(p) && _gitStatusByPath.TryGetValue(p, out var st))
                    vm.GitStatus = st;
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

        // ---- pinned tags ----

        private List<string> _pinnedTags = new List<string>();

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
            PinnedSections.Clear();
            foreach (var tag in _pinnedTags)
            {
                var section = new PinnedSectionViewModel(tag, new RelayCommand(() => Unpin(tag)));
                PinnedSections.Add(section);
                RefreshPinnedSection(section);
            }
        }

        private void RefreshPinnedSection(PinnedSectionViewModel section)
        {
            if (_store == null) return;
            try
            {
                var query = SearchQueryParser.Parse("#" + section.Tag);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var (where, pars) = SearchQueryParser.ToSql(query, nowMs);
                var tabs = _store.ListTabs(where, pars, "ts DESC");
                var rows = tabs.Select(MakeRowVm).ToList();
                ReplaceCollection(section.Items, rows);
                KickGitStatusUpdate(rows);
            }
            catch (Exception ex)
            {
                _log?.Error("RefreshPinnedSection failed for #" + section.Tag, ex);
            }
        }

        private void RefreshAllPinnedSections()
        {
            foreach (var s in PinnedSections) RefreshPinnedSection(s);
        }

        private void PinNewTag()
        {
            var allTags = _store?.GetAllTags() ?? new List<string>();
            var picked = _showTagPicker?.Invoke(allTags, _pinnedTags);
            if (picked == null) return;
            _pinnedTags = picked.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SavePinnedTags();
            RebuildPinnedSections();
        }

        private void TogglePin(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            if (_pinnedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                _pinnedTags.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
            else
                _pinnedTags.Add(tag);
            SavePinnedTags();
            RebuildPinnedSections();
        }

        private void Unpin(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            _pinnedTags.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
            SavePinnedTags();
            RebuildPinnedSections();
        }

        public IReadOnlyList<string> CurrentPinnedTags => _pinnedTags.AsReadOnly();

        // ---- open / commands ----

        private void OpenTab(TabRowViewModel vm)
        {
            if (vm == null) return;
            OpenTabById(vm.Source?.TabId);
        }

        public void OpenTabById(string tabId)
        {
            if (string.IsNullOrEmpty(tabId)) return;
            _ = _openTabId?.Invoke(tabId);
        }

        private void OpenAsNew(TabRowViewModel vm)
        {
            if (vm == null) return;
            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new KeyValuePair<string, object>("$t", vm.Source.TabId) }, 1);
            if (snaps.Count > 0 && _openAsNewSnapshot != null) _ = _openAsNewSnapshot(snaps[0]);
        }

        private void DeleteTabRow(TabSummary t)
        {
            if (t == null || _store == null || string.IsNullOrEmpty(t.TabId)) return;

            var name = string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name;
            var message = $"Delete \"{name}\" from history?\n\nAll snapshots for this tab will be permanently removed.";
            if (t.IsOpen)
                message += "\n\nNote: the tab is still open in SSMS — a fresh entry will appear after the next snapshot.";

            var ok = _confirm?.Invoke("Delete from history", message) ?? false;
            if (!ok) return;

            try
            {
                _store.DeleteTab(t.TabId);
                _log?.Info($"User deleted tab from history: {t.TabId} ({name})");
            }
            catch (Exception ex)
            {
                _log?.Error($"DeleteTab failed for {t.TabId}", ex);
                Info("Delete failed: " + ex.Message);
                return;
            }
            RefreshAll();
        }

        /// <summary>Unwrap a row VM back to its <see cref="TabSummary"/>. The TabRowContextMenu
        /// is shared by both the recent list (TabRowViewModel) and the stored-queries list
        /// (StoredQueryRowViewModel), so commands that operate on the underlying tab need to
        /// accept either.</summary>
        private static TabSummary SourceOf(object p)
        {
            if (p is TabRowViewModel t) return t.Source;
            if (p is StoredQueryRowViewModel s) return s.Source;
            return null;
        }

        private void OpenStorage()
        {
            var path = _store?.Root;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                var safe = path.Replace("\"", "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{safe}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _log?.Error("Open storage folder failed", ex); }
        }

        private void OpenConfigFolder()
        {
            var file = _settings?.FilePath;
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                var safe = dir.Replace("\"", "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{safe}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _log?.Error("Open config folder failed", ex); }
        }

        private void RevealSnapshot()
        {
            var path = _store?.Root;
            if (string.IsNullOrEmpty(path)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path.Replace("\"", "")}\""); } catch { }
        }

        private static void Copy(string text)
        {
            try { Clipboard.SetText(text ?? ""); } catch { }
        }

        // ---- save to scripts ----

        private void SaveTabToScriptsFolder(TabSummary t)
        {
            if (t == null || _store == null || _settings == null) return;

            var s = _settings.Load();
            var folderPath = s.SavedScripts?.FolderPath;
            var treeFolder = string.IsNullOrWhiteSpace(s.SavedScripts?.TreeFolder)
                ? "Saved Scripts" : s.SavedScripts.TreeFolder.Trim().Trim('/');

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                folderPath = Path.Combine(docs, "AutoTabOrganiser", "Scripts");
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
            try { Directory.CreateDirectory(folderPath); }
            catch (Exception ex)
            {
                _log?.Error("Create scripts folder failed: " + folderPath, ex);
                Info("Could not create or access " + folderPath + ": " + ex.Message);
                return;
            }

            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new KeyValuePair<string, object>("$t", t.TabId) }, 1);
            if (snaps.Count == 0) { Info("No snapshot is available for this tab yet."); return; }
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

            // Combine existing-tab subfolders with on-disk filesystem folders so the user
            // can save into a folder that doesn't yet have any tabs in the index. Skip any
            // path that contains a dot-prefixed segment (.git, .vscode, .archive...) — those
            // shouldn't be offered as save targets.
            var existing = _store.ListTabs("folder LIKE $fp AND folder != $f",
                new[]
                {
                    new KeyValuePair<string, object>("$fp", treeFolder + "/%"),
                    new KeyValuePair<string, object>("$f",  treeFolder)
                }, "folder ASC");
            var subfoldersFromIndex = existing
                .Select(tab => tab.Folder.Substring(treeFolder.Length + 1))
                .Where(sub => !string.IsNullOrEmpty(sub));

            var subfoldersFromDisk = EnumerateSavableSubfolders(folderPath);

            var subfolders = subfoldersFromIndex
                .Concat(subfoldersFromDisk)
                .Where(sub => !ContainsDotSegment(sub))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(sub => sub, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ReplaceCollection(SaveSubfolderItems, subfolders);

            SaveSubfolder = (!string.IsNullOrEmpty(t.Folder) && t.Folder.StartsWith(treeFolder + "/", StringComparison.OrdinalIgnoreCase))
                ? t.Folder.Substring(treeFolder.Length + 1)
                : "";
            SaveFileName = defaultName;
            SavePromptHint = "Will be written to: " + folderPath;
            SavePromptVisible = true;
            _onSavePromptShown?.Invoke();
        }

        private void ConfirmSaveScripts()
        {
            var t = _pendingSaveTab;
            if (t == null) { ClearPendingSave(); return; }

            var raw = (SaveFileName ?? "").Trim();
            if (raw.Length == 0) return;
            var picked = PathSanitiser.Sanitise(raw) ?? _pendingSaveDefaultName;

            var folderPath = _pendingSaveFolderPath;
            var treeFolder = _pendingSaveTreeFolder;
            var content = _pendingSaveContent ?? string.Empty;

            var rawSub = (SaveSubfolder ?? "").Trim().Trim('/', '\\');
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

            // Containment guard: segment sanitisation can't stop a drive-rooted subfolder
            // ("C:\Elsewhere" splits into segments that survive Sanitise, and Path.Combine
            // yields the rooted path). Saved scripts must only ever land under the
            // configured folder.
            try
            {
                var rootFull = Path.GetFullPath(folderPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var subFull  = Path.GetFullPath(subDirAbs).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (!subFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    Info("Subfolder must be inside the Stored Queries folder (" + folderPath + ").");
                    return;
                }
            }
            catch (Exception ex)
            {
                Info("Invalid subfolder: " + ex.Message);
                return;
            }

            Directory.CreateDirectory(subDirAbs);

            var fileName = picked + ".sql";
            var targetPath = Path.Combine(subDirAbs, fileName);
            var folderForMeta = string.IsNullOrEmpty(sanitisedSub)
                ? treeFolder
                : treeFolder + "/" + sanitisedSub;

            // Clobber guard: saving under a name that already belongs to a DIFFERENT query
            // would silently destroy that file. Same-tab overwrite (re-saving your own
            // script) stays frictionless.
            if (File.Exists(targetPath))
            {
                var ownPath = LookupOriginalFilePath(t);
                bool samePath = false;
                try
                {
                    samePath = !string.IsNullOrEmpty(ownPath)
                               && string.Equals(Path.GetFullPath(ownPath), Path.GetFullPath(targetPath),
                                                StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                if (!samePath)
                {
                    var ok = _confirm?.Invoke("Overwrite existing file",
                        $"\"{fileName}\" already exists in that folder and belongs to a different query.\n\n" +
                        "Overwrite it? The existing file's content will be lost.") ?? false;
                    if (!ok) return;
                }
            }

            var newContent = MetadataWriter.SetFolder(content, folderForMeta);
            newContent = MetadataWriter.SetFile(newContent, fileName);
            if (!string.IsNullOrEmpty(t.TabId))
                newContent = MetadataWriter.SetId(newContent, t.TabId);

            // Persist the connection name with the saved file so reopening surfaces the
            // original SSMS connection even before the user reconnects.
            if (!string.IsNullOrEmpty(t.Server))
                newContent = MetadataWriter.SetServer(newContent, t.Server);
            if (!string.IsNullOrEmpty(t.Database))
                newContent = MetadataWriter.SetDatabase(newContent, t.Database);

            try
            {
                File.WriteAllText(targetPath, newContent, new UTF8Encoding(false));
                _log?.Info($"Saved tab {t.TabId} to {targetPath}");
            }
            catch (Exception ex)
            {
                _log?.Error("Write to scripts folder failed: " + targetPath, ex);
                Info("Could not write file: " + ex.Message);
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
            Info("Saved " + fileName);
            // RefreshAll so the new file shows in StoredQueries with fresh git status, plus
            // the git icon on the same tab in RECENT / search / pinned updates immediately.
            RefreshAll();
            // Open the freshly-written file in SSMS so focus switches to the saved instance.
            // OpenTabFromHistoryAsync prefers the saved-reason snapshot's on-disk path (just
            // indexed above), so this surfaces the canonical file rather than a scratch copy.
            OpenTabById(t.TabId);
        }

        private void ClearPendingSave()
        {
            SavePromptVisible = false;
            SaveSubfolder = "";
            SaveSubfolderItems.Clear();
            _pendingSaveTab = null;
            _pendingSaveContent = null;
            _pendingSaveFolderPath = null;
            _pendingSaveTreeFolder = null;
            _pendingSaveDefaultName = null;
        }

        /// <summary>
        /// Open the system folder browser rooted at the configured Stored Queries folder
        /// and set <see cref="SaveSubfolder"/> to the picked path's relative location. The
        /// user can browse into existing folders or create new ones via the dialog itself.
        /// </summary>
        private void BrowseSaveFolder()
        {
            if (string.IsNullOrEmpty(_pendingSaveFolderPath)) return;
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Choose a subfolder under " + _pendingSaveFolderPath;
                    dlg.SelectedPath = _pendingSaveFolderPath;
                    dlg.ShowNewFolderButton = true;
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    var picked = dlg.SelectedPath;
                    if (string.IsNullOrEmpty(picked)) return;

                    // Compute path relative to the stored-queries root. If the user navigated
                    // outside the root, just use the absolute path verbatim — ConfirmSaveScripts
                    // will sanitise it segment-by-segment and combine with the root.
                    var root = Path.GetFullPath(_pendingSaveFolderPath);
                    var full = Path.GetFullPath(picked);
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        var rel = full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                        SaveSubfolder = rel;
                    }
                    else
                    {
                        SaveSubfolder = picked;
                    }
                }
            }
            catch (Exception ex) { _log?.Error("Browse save folder failed", ex); }
        }

        /// <summary>
        /// Enumerate filesystem subfolders under <paramref name="root"/> recursively, skipping
        /// any folder whose name starts with '.' (and any descendants). Returns relative paths
        /// with forward slashes.
        /// </summary>
        private static IEnumerable<string> EnumerateSavableSubfolders(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
            // Skip reparse points and cap depth: this runs on the UI thread when the save
            // panel opens, and a junction loop under the scripts folder would hang SSMS.
            const int maxDepth = 32;
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                if (depth >= maxDepth) continue;
                IEnumerable<string> children = null;
                try { children = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (var sub in children)
                {
                    var name = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
                    bool reparse = false;
                    try { reparse = (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0; } catch { }
                    if (reparse) continue;
                    var rel = sub.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    yield return rel;
                    stack.Push((sub, depth + 1));
                }
            }
        }

        /// <summary>True if any forward- or backslash-delimited segment of <paramref name="rel"/> starts with '.'.</summary>
        private static bool ContainsDotSegment(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            foreach (var seg in rel.Split('/', '\\'))
                if (!string.IsNullOrEmpty(seg) && seg.StartsWith(".")) return true;
            return false;
        }

        // ---- stored queries / git ----

        /// <summary>
        /// Returns true if <paramref name="msg"/> satisfies the minimum-length rule from
        /// <see cref="GitSettings"/>. On failure, surfaces a one-liner via <see cref="Info"/>
        /// explaining why and returns false so callers can bail before invoking git. Trims
        /// whitespace before counting so "    " doesn't pass a 4-char minimum.
        /// </summary>
        private bool ValidateCommitMessage(string msg)
        {
            var s = _settings?.Load();
            var g = s?.Git;
            if (g == null || !g.EnforceMinCommitMessage) return true;
            var min = g.MinCommitMessageLength;
            if (min <= 0) return true;
            var len = (msg ?? "").Trim().Length;
            if (len >= min) return true;
            Info($"Commit message must be at least {min} character{(min == 1 ? "" : "s")} (got {len}). " +
                 $"Disable in settings.json under git.enforceMinCommitMessage.");
            return false;
        }

        private void StageStoredQuery(StoredQueryRowViewModel vm)
        {
            if (vm == null) return;
            if (string.IsNullOrEmpty(vm.FilePath) || !File.Exists(vm.FilePath))
            {
                Info("File not found: " + vm.FilePath); return;
            }
            var r = GitHelper.Add(vm.FilePath, _log);
            if (!r.Ok) { Info("git add failed: " + (r.StdErr ?? r.Error ?? r.StdOut)); return; }

            // Instant, zero-I/O visual feedback. The FS watcher's debounced refresh will
            // catch any structural changes (new files, removed files) shortly after.
            OptimisticSetGitStatus(vm.FilePath, vm.Source?.TabId, GitFileStatus.Staged);
            _gitResolver.Invalidate();
            Info("Staged " + Path.GetFileName(vm.FilePath));
        }

        private void CommitStoredQuery(StoredQueryRowViewModel vm)
        {
            if (vm == null) return;
            if (string.IsNullOrEmpty(vm.FilePath) || !File.Exists(vm.FilePath))
            {
                Info("File not found: " + vm.FilePath); return;
            }
            var msg = (CommitMessage ?? "").Trim();
            if (string.IsNullOrEmpty(msg))
                msg = "Update " + (vm.Source.Name ?? Path.GetFileNameWithoutExtension(vm.FilePath));
            if (!ValidateCommitMessage(msg)) return;

            var added = GitHelper.Add(vm.FilePath, _log);
            if (!added.Ok) { Info("git add failed: " + (added.StdErr ?? added.Error ?? added.StdOut)); return; }
            var commit = GitHelper.Commit(vm.FilePath, msg, _log);
            if (!commit.Ok) { Info("git commit failed: " + (commit.StdErr ?? commit.Error ?? commit.StdOut)); return; }

            OptimisticSetGitStatus(vm.FilePath, vm.Source?.TabId, GitFileStatus.Clean);
            _gitResolver.Invalidate();
            Info("Committed " + Path.GetFileName(vm.FilePath));
        }

        /// <summary>
        /// Resolve the diff for <paramref name="vm"/>'s file against HEAD off the UI thread,
        /// then hand the text to <see cref="_showGitDiff"/> for the view to display.
        /// Untracked files don't have a HEAD-vs-working-tree diff, so we synthesize a
        /// "+"-prefixed diff from the file contents instead.
        /// </summary>
        private void DiffStoredQuery(StoredQueryRowViewModel vm)
        {
            if (vm == null || string.IsNullOrEmpty(vm.FilePath)) return;
            if (_showGitDiff == null) return;

            var path = vm.FilePath;
            var status = vm.Status;
            var fileName = Path.GetFileName(path);

            Task.Run(() =>
            {
                string diff;
                try
                {
                    if (status == GitFileStatus.Untracked)
                    {
                        diff = SyntheticUntrackedDiff(path);
                    }
                    else
                    {
                        var r = GitHelper.Diff(path, _log);
                        if (!string.IsNullOrEmpty(r.Error))
                            diff = "git diff failed: " + r.Error;
                        else if (r.ExitCode != 0)
                            diff = "git diff exited " + r.ExitCode + ":\n" + (r.StdErr ?? r.StdOut ?? "");
                        else
                            diff = string.IsNullOrEmpty(r.StdOut)
                                ? "(no changes vs HEAD)"
                                : r.StdOut;
                    }
                }
                catch (Exception ex) { diff = "Error: " + ex.Message; }

                Marshal(() => _showGitDiff(fileName, diff ?? "", status));
            });
        }

        /// <summary>Build a "+"-prefixed diff for an untracked file from its on-disk content.</summary>
        private static string SyntheticUntrackedDiff(string filePath)
        {
            if (!File.Exists(filePath)) return "[file not found: " + filePath + "]";
            string content;
            try { content = File.ReadAllText(filePath); }
            catch (Exception ex) { return "Could not read file: " + ex.Message; }

            var sb = new StringBuilder();
            sb.AppendLine("--- /dev/null");
            sb.AppendLine("+++ " + filePath);
            sb.AppendLine("@@ untracked file @@");
            foreach (var line in content.Split(new[] { '\n' }, StringSplitOptions.None))
                sb.Append('+').AppendLine(line.TrimEnd('\r'));
            return sb.ToString();
        }

        private void CommitAllStoredQueries()
        {
            var rows = StoredQueries.ToList();
            if (rows.Count == 0) return;

            var msg = (CommitMessage ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) msg = "Update stored queries";
            if (!ValidateCommitMessage(msg)) return;

            var byRepo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.FilePath) || !File.Exists(r.FilePath)) continue;
                var repo = GitHelper.FindRepoRoot(Path.GetDirectoryName(r.FilePath));
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
                    if (!added.Ok) { Info("git add failed for " + p + ": " + (added.StdErr ?? added.Error)); return; }
                }
                var safe = GitHelper.EscapeArg(msg);
                var quoted = string.Join(" ", pair.Value.Select(p => "\"" + GitHelper.EscapeArg(p) + "\""));
                var result = GitHelper.Run(repo, $"commit -m \"{safe}\" -- {quoted}", _log);
                if (!result.Ok) { Info("git commit failed in " + repo + ": " + (result.StdErr ?? result.Error ?? result.StdOut)); return; }
            }

            CommitMessage = "";

            // Optimistic: every row we just committed becomes Clean — drop them all.
            foreach (var r in rows.ToList())
                OptimisticSetGitStatus(r.FilePath, r.Source?.TabId, GitFileStatus.Clean);

            _gitResolver.Invalidate();
        }

        /// <summary>Open the configured Stored Queries folder in Explorer.</summary>
        private void OpenStoredQueriesFolder()
        {
            try
            {
                var path = ResolveStoredQueriesPath();
                Directory.CreateDirectory(path);
                // Strip any embedded quote chars before interpolating (Windows paths can't
                // contain them anyway, but settings.json is editable so we don't trust input).
                var safe = path.Replace("\"", "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{safe}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _log?.Error("Open stored-queries folder failed", ex); }
        }

        /// <summary>
        /// Open a terminal at the configured Stored Queries folder. Tries Windows Terminal
        /// (<c>wt.exe</c>) first since it's the modern default on Win11/recent Win10 dev
        /// installs; falls back to PowerShell, then cmd.exe.
        /// </summary>
        private void OpenStoredQueriesTerminal()
        {
            string path;
            try
            {
                path = ResolveStoredQueriesPath();
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) { _log?.Error("Resolve stored-queries path failed", ex); return; }

            // Each launcher uses ShellExecute (UseShellExecute=true) so PATH/App-Execution-
            // Alias resolution applies and we don't have to know the absolute path of wt.exe.
            // Strip embedded quotes before interpolating into the wt.exe arg string —
            // settings.json is user-editable, defence in depth.
            var safePath = path.Replace("\"", "");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"-d \"{safePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }
            catch { /* Windows Terminal not installed — fall through */ }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoExit")
                {
                    UseShellExecute = true,
                    WorkingDirectory = path
                });
                return;
            }
            catch { /* fall through to cmd */ }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/K")
                {
                    UseShellExecute = true,
                    WorkingDirectory = path
                });
            }
            catch (Exception ex) { _log?.Error("Open terminal failed", ex); }
        }

        private string ResolveStoredQueriesPath()
        {
            var s = _settings?.Load();
            var path = s?.SavedScripts?.FolderPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                path = Path.Combine(docs, "AutoTabOrganiser", "Scripts");
            }
            return path;
        }

        private void RunGit(TabSummary t, string verb, Func<string, GitResult> op)
        {
            if (t == null) return;
            var path = LookupOriginalFilePath(t);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Info("This tab has no file on disk yet (untitled). Save it first.");
                return;
            }
            var repo = GitHelper.FindRepoRoot(Path.GetDirectoryName(path));
            if (repo == null) { Info("No git repository found containing " + path); return; }
            try
            {
                var r = op(path);
                if (!r.Ok)
                {
                    // Surface every kind of failure: the GitResult.Error string (set when we
                    // never started the process), or the process's stderr/stdout, or just the
                    // exit code. The previous logic gated on Error being empty and silently
                    // swallowed any failure that *did* populate it.
                    var why = !string.IsNullOrEmpty(r.Error) ? r.Error
                            : !string.IsNullOrEmpty(r.StdErr) ? r.StdErr
                            : !string.IsNullOrEmpty(r.StdOut) ? r.StdOut
                            : ("exit code " + r.ExitCode);
                    // Sentinels for user-initiated stops (cancel from prompt, validation
                    // rejection): the lambda already informed the user via Info, don't
                    // pile on a second "git X failed:" line.
                    if (why == "(silent)") return;
                    Info($"git {verb} failed: {why}");
                    return;
                }

                // Optimistic feedback per verb so the row visibly changes immediately.
                // FS watcher catches any structural changes (new files etc.) on its own.
                if (verb == "add")    OptimisticSetGitStatus(path, t.TabId, GitFileStatus.Staged);
                if (verb == "commit") OptimisticSetGitStatus(path, t.TabId, GitFileStatus.Clean);

                _gitResolver.Invalidate();
                Info($"git {verb}: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                _log?.Error("git command failed", ex);
                Info($"git {verb} failed: {ex.Message}");
            }
        }

        // ---- info bar ----

        private void Info(string message)
        {
            // Forward to the view's local impl too — keeps any external callers (package) working.
            _showInfo?.Invoke(message);
            ShowInfoInternal(message);
        }

        public void ShowInfoExternal(string message) => ShowInfoInternal(message);

        private void ShowInfoInternal(string message)
        {
            if (string.IsNullOrEmpty(message)) { HideInfoBar(); return; }
            InfoMessage = message;
            InfoBarVisible = true;
            if (_infoBarTimer == null)
            {
                _infoBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _infoBarTimer.Tick += (s, e) => { _infoBarTimer.Stop(); InfoBarVisible = false; };
            }
            _infoBarTimer.Stop();
            _infoBarTimer.Start();
        }

        private void HideInfoBar()
        {
            _infoBarTimer?.Stop();
            InfoBarVisible = false;
        }

        // ---- last-snapshot indicator ----
        // Drives the toolbar "Snapshotted Xs ago" text. UpdateLastSnapshotFromRecent runs after
        // each RefreshRecent (called on every snapshot via the package onTabUpdated callback);
        // a 1s DispatcherTimer re-emits LastSnapshotText so the relative formatting stays
        // current between snapshots.

        private DateTime? _lastSnapshotUtc;
        public DateTime? LastSnapshotUtc
        {
            get => _lastSnapshotUtc;
            private set
            {
                if (_lastSnapshotUtc == value) return;
                _lastSnapshotUtc = value;
                Notify();
                Notify(nameof(LastSnapshotText));
                Notify(nameof(LastSnapshotTooltip));
            }
        }

        public string LastSnapshotText
        {
            get
            {
                if (!_lastSnapshotUtc.HasValue) return "—";
                var ms = new DateTimeOffset(DateTime.SpecifyKind(_lastSnapshotUtc.Value, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds();
                return "Snapshotted " + RelativeTime.Format(ms);
            }
        }

        public string LastSnapshotTooltip
            => _lastSnapshotUtc.HasValue
                ? _lastSnapshotUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "No snapshot taken in this session yet.";

        private DispatcherTimer _lastSnapshotTimer;

        private void EnsureLastSnapshotTimer()
        {
            if (_lastSnapshotTimer != null) return;
            _lastSnapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            // LastBackupText's getter throttles its own disk probe to every 5 minutes, so
            // notifying it on the same 1s tick costs nothing between probes.
            _lastSnapshotTimer.Tick += (s, e) =>
            {
                Notify(nameof(LastSnapshotText));
                Notify(nameof(LastBackupText));
                Notify(nameof(LastBackupTooltip));
            };
            _lastSnapshotTimer.Start();
        }

        private void UpdateLastSnapshotFromRecent()
        {
            long maxMs = 0;
            foreach (var row in Recent)
            {
                var ts = row?.Source?.Ts ?? 0;
                if (ts > maxMs) maxMs = ts;
            }
            if (maxMs <= 0) { LastSnapshotUtc = null; return; }
            LastSnapshotUtc = DateTimeOffset.FromUnixTimeMilliseconds(maxMs).UtcDateTime;
        }

        // ---- last-backup indicator + export ----
        // Makes the safety net visible: the toolbar shows when the pre-migration DB backup
        // last ran, and the export command produces a portable zip of the entire store.

        private DateTime? _lastBackupUtc;
        private int _lastBackupCheckedTick = int.MinValue;
        private const int BackupCheckIntervalMs = 5 * 60 * 1000;

        public string LastBackupText
        {
            get
            {
                MaybeRefreshLastBackup();
                if (!_lastBackupUtc.HasValue) return "no backup yet";
                var ms = new DateTimeOffset(DateTime.SpecifyKind(_lastBackupUtc.Value, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds();
                return "backup " + RelativeTime.Format(ms);
            }
        }

        public string LastBackupTooltip
            => _lastBackupUtc.HasValue
                ? "Pre-migration DB backup written " + _lastBackupUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                  + "\nBackups live under " + (_store?.Root ?? "?") + "\\backups"
                : "No upgrade backup exists yet — one is written automatically before any version migration."
                  + "\nUse the Export button for an on-demand portable archive.";

        /// <summary>Directory mtime probe, throttled to every 5 minutes; tick-driven.</summary>
        private void MaybeRefreshLastBackup()
        {
            var now = Environment.TickCount;
            if (_lastBackupCheckedTick != int.MinValue
                && unchecked(now - _lastBackupCheckedTick) < BackupCheckIntervalMs) return;
            _lastBackupCheckedTick = now;
            try { _lastBackupUtc = _store?.GetLastBackupTimeUtc(); } catch { }
        }

        private void ExportArchive()
        {
            if (_store == null) return;
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Auto Tab Organiser archive",
                    FileName = "AutoTabOrganiser-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                    Filter = "Zip archive (*.zip)|*.zip"
                };
                if (dlg.ShowDialog() != true) return;
                var dest = dlg.FileName;

                Info("Exporting archive…");
                Task.Run(() =>
                {
                    try
                    {
                        _store.ExportArchive(dest, _settings?.FilePath);
                        Marshal(() => Info("Export complete: " + dest));
                    }
                    catch (Exception ex)
                    {
                        _log?.Error("Export archive failed", ex);
                        Marshal(() => Info("Export failed: " + ex.Message));
                    }
                });
            }
            catch (Exception ex)
            {
                _log?.Error("Export archive failed", ex);
                Info("Export failed: " + ex.Message);
            }
        }

        // ---- stored-queries branch status + push ----

        private string _storedQueriesBranchText = "";
        /// <summary>"main · ↑2 ↓1" for the stored-queries repo; empty when not in a repo.</summary>
        public string StoredQueriesBranchText
        {
            get => _storedQueriesBranchText;
            private set { if (_storedQueriesBranchText == value) return; _storedQueriesBranchText = value; Notify(); Notify(nameof(StoredQueriesPushVisible)); }
        }

        public bool StoredQueriesPushVisible => !string.IsNullOrEmpty(_storedQueriesBranchText);

        /// <summary>
        /// Push the stored-queries repo. STRICTLY user-initiated — this is the only place in
        /// the extension that can cause network traffic, it runs the user's own git client
        /// against the remote they configured, and only on an explicit button click.
        /// </summary>
        private void PushStoredQueries()
        {
            var repo = GitHelper.FindRepoRoot(ResolveStoredQueriesPath());
            if (repo == null) { Info("Stored Queries folder is not inside a git repository."); return; }
            Info("Pushing…");
            Task.Run(() =>
            {
                var r = GitHelper.Run(repo, "push", _log);
                Marshal(() =>
                {
                    if (r.Ok) Info("Pushed.");
                    else Info("git push failed: " + FirstNonEmpty(r.StdErr, r.Error, r.StdOut, "exit " + r.ExitCode));
                    _gitResolver.Invalidate();
                    RefreshStoredQueries();
                });
            });
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        // ---- helpers ----

        private string LookupOriginalFilePath(TabSummary t)
        {
            if (t == null) return null;

            // Hot path: TabSummary already carries the denormalised saved/latest paths from
            // ListTabs (scalar subqueries served by composite indexes). Prefer the most-recent
            // saved-reason file_path *if still on disk* — after a user saves a tab and edits
            // it in SSMS, subsequent edit snapshots record the SSMS temp moniker as file_path,
            // which would mislead git lookups. The check against File.Exists discards stale
            // entries (file deleted/moved). Fall back to the latest snapshot's file_path for
            // tabs that have never been explicitly saved.
            if (!string.IsNullOrEmpty(t.SavedFilePath) && File.Exists(t.SavedFilePath))
                return t.SavedFilePath;
            return t.LatestFilePath;
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> rows)
        {
            target.Clear();
            foreach (var r in rows) target.Add(r);
        }

        private void Marshal(Action a)
        {
            var d = _dispatcher ?? Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a();
            else d.BeginInvoke(a);
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
