using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tracking;
using AutoTabOrganiser.UI;
using AutoTabOrganiser.Util;
using Task = System.Threading.Tasks.Task;

namespace AutoTabOrganiser
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Auto Tab Organiser", "Auto-snapshots and organises SSMS query tabs.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    // MultiInstances = true permits opening additional copies of the tool window via the
    // "New View" toolbar button. Each instance has its own id (0, 1, 2…) and gets its own
    // ToolWindowControl + ViewModel, but they all share the same SnapshotStore / SettingsStore
    // and stay live across SSMS restarts only at id=0 (id>0 are session-scoped).
    [ProvideToolWindow(typeof(TabOrganiserToolWindow), MultiInstances = true, Style = VsDockStyle.Tabbed, Window = "3AE79031-E1BC-11D0-8F78-00A0C9110057")]
    [Guid(PackageGuids.AutoTabOrganiserPackageString)]
    public sealed class AutoTabOrganiserPackage : AsyncPackage
    {
        private const string OutputPaneTitle = "Auto Tab Organiser";
        private static readonly Guid OutputPaneGuid = new Guid("9d2d9b51-7b7e-4b6c-9b0a-2c5b2d8a1f3e");

        private IVsOutputWindowPane _pane;
        private SettingsStore _settings;
        private SnapshotStore _store;
        private DocumentTracker _docTracker;
        private Pruner _pruner;
        private Logger _log;
        private Timer _pruneTimer;
        private EnvDTE.WindowEvents _windowEvents;
        private EnvDTE.DTEEvents _dteEvents;
        // WPF-level keyboard hook for the Quick Switcher chord in the NON-editor scope (focus
        // not in a query window). Installed via InputManager.PreProcessInput, which fires
        // before WPF command routing. The in-editor scope is the MEF QuickSwitchKeyProcessor;
        // both read the same Ui.QuickSwitchHotkey setting.
        private System.Windows.Input.PreProcessInputEventHandler _quickSwitchPreProcessHook;
        // Parsed Quick Switcher chord (from Ui.QuickSwitchHotkey). Drives the non-editor WPF
        // hook below; the in-editor path is the MEF QuickSwitchKeyProcessor, which reads the
        // same setting. Key.None means "no in-shell hotkey configured".
        private System.Windows.Input.ModifierKeys _quickSwitchModifiers;
        private System.Windows.Input.Key _quickSwitchKey;
        // Set when DTE fires OnBeginShutdown. Read by DocumentTracker to skip the
        // close-snapshot cascade as SSMS tears down each open tab — those snapshots are
        // duplicates of the latest content and just slow down shutdown.
        internal volatile bool IsShuttingDown;

        // Tabs flagged open by the previous session, captured before ClearAllOpenFlags.
        // Consumed (and nulled) when the user accepts or dismisses the restore offer.
        private List<TabSummary> _previousSessionTabs;
        private bool _sessionRestoreOffered;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _pane = await CreateOutputPaneAsync(cancellationToken);

            _settings = new SettingsStore(SettingsStore.DefaultSettingsFilePath());
            var appSettings = _settings.Load();

            // Parse the configurable Quick Switcher chord once for the non-editor WPF hook.
            if (!AutoTabOrganiser.Util.HotkeyChord.TryParse(appSettings.Ui.QuickSwitchHotkey,
                    out _quickSwitchModifiers, out _quickSwitchKey))
            {
                _quickSwitchKey = System.Windows.Input.Key.None;
            }

            var storageRoot = _settings.ResolveStorageLocation();
            Directory.CreateDirectory(storageRoot);

            var logsDir = Path.Combine(Path.GetDirectoryName(_settings.FilePath), "logs");
            _log = new Logger(logsDir, msg =>
            {
                var pane = _pane;
                if (pane == null) return;
#pragma warning disable VSTHRD010
                pane.OutputStringThreadSafe(msg + Environment.NewLine);
#pragma warning restore VSTHRD010
            }, debugEnabled: false);

            _log.Info($"Loaded. storage='{storageRoot}', settings='{_settings.FilePath}'.");

            // Always register commands first so the user gets feedback even if storage init fails.
            await RegisterCommandsAsync(cancellationToken);

            try
            {
                _store = new SnapshotStore(storageRoot, _log);
                // Capture which tabs the previous session had open BEFORE the flags are
                // cleared — this list powers the "Reopen tabs from last session" banner.
                // Survives crash and clean shutdown alike (the shutdown path skips per-tab
                // close handling, so is_open stays 1 either way).
                try { _previousSessionTabs = _store.GetOpenTabs(); }
                catch (Exception ex) { _log.Warn("Previous-session tab capture failed: " + ex.Message); }
                _store.ClearAllOpenFlags();
            }
            catch (Exception ex)
            {
                _log.Error("SnapshotStore initialisation failed", ex);
                _log.Error("The Tab Organiser tool window will be empty. Snapshots disabled.");
                return;
            }

            var componentModel = (IComponentModel)await GetServiceAsync(typeof(SComponentModel));
            var adapterFactory = componentModel?.GetService<IVsEditorAdaptersFactoryService>();

            _docTracker = await DocumentTracker.CreateAsync(this, adapterFactory, _store, _settings, _log,
                onTabUpdated: tabId => RefreshToolWindowAsync(),
                onTabClosed:  tabId => RefreshToolWindowAsync(),
                isShuttingDown: () => IsShuttingDown,
                cancellationToken);

            // Subscribe to SSMS window-activation so we can pin the user's currently-focused
            // SQL tab to the top of the RECENT section. DTE.Events references must be held
            // (not GC'd) for the events to keep firing.
            //
            // We capture the OnBeginShutdown handler in a named field so Dispose can unsubscribe
            // it — an anonymous lambda has no identity for `-=` and would otherwise leak into a
            // disposed package instance if SSMS reloads the extension.
            try
            {
                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                if (dte != null)
                {
                    _windowEvents = dte.Events.WindowEvents;
                    _windowEvents.WindowActivated += OnWindowActivated;

                    // OnBeginShutdown fires before SSMS starts tearing down tabs, so by the time
                    // DocumentTracker sees OnBeforeLastDocumentUnlock for each doc the flag is set.
                    _dteEvents = dte.Events.DTEEvents;
                    _dteEvents.OnBeginShutdown += OnDteBeginShutdown;
                }
            }
            catch (Exception ex) { _log.Debug("WindowEvents subscribe failed: " + ex.Message); }

            // Quick Switcher hotkey, NON-editor scope (tool window, no document focused). The
            // in-editor scope is handled by the MEF QuickSwitchKeyProcessor — a bound editor
            // command is translated before WPF input, so a global hook can't win there, which
            // is why the hotkey must be a chord that's unassigned in the Text Editor scope.
            // This hook covers the case where focus isn't in the editor. Installed at
            // InputManager.PreProcessInput, which runs before command routing.
            try
            {
                if (_quickSwitchKey != System.Windows.Input.Key.None)
                {
                    _quickSwitchPreProcessHook = OnPreProcessInputForQuickSwitch;
                    System.Windows.Input.InputManager.Current.PreProcessInput += _quickSwitchPreProcessHook;
                    _log.Info($"Quick Switcher hotkey hook installed for '{appSettings.Ui.QuickSwitchHotkey}'.");
                }
            }
            catch (Exception ex) { _log.Debug("Quick Switcher hotkey hook install failed: " + ex.Message); }

            ScheduleDailyPrune(appSettings);

            // Best-effort: clear out forgotten files in the open/ folder so it doesn't grow
            // unbounded. Runs in the background so init doesn't block on disk I/O.
            _ = Task.Run(() => PruneStaleOpenFiles());

            // Best-effort: delete snapshot files no DB row references (crash between disk
            // write and DB insert, or a file delete that lost a race). The 7-day age floor
            // guarantees an in-flight write can never be swept.
            _ = Task.Run(() =>
            {
                try { _store.SweepOrphanSnapshotFiles(TimeSpan.FromDays(7)); }
                catch (Exception ex) { _log?.Debug("Orphan snapshot sweep failed: " + ex.Message); }
            });

            // Best-effort: scan the saved-scripts folder and import any .sql files that aren't
            // already represented in the DB. Useful when:
            //   - the user restores a backup of their saved-scripts folder onto a fresh install,
            //   - they drop .sql files into the folder via Explorer/another tool,
            //   - the DB was reset but the scripts on disk survived.
            // Idempotent — files already represented by a saved-reason snapshot are skipped.
            //
            // Capture the currently-open document paths on the UI thread BEFORE handing off to
            // a background task. The import path may inject `@id` into files lacking one via
            // File.WriteAllText; if SSMS has the file open with unsaved edits, that overwrite
            // would trigger a reload prompt and could lose those edits. Skipping the disk write
            // for open files keeps the data-safety invariant intact.
            var openDocPaths = TryEnumerateOpenDocumentPaths();
            var settingsLoaded = _settings.Load();
            _ = Task.Run(() =>
            {
                try { ImportSavedScriptsFolder(settingsLoaded, openDocPaths); }
                catch (Exception ex) { _log?.Warn("Saved-scripts import failed: " + ex.Message); }
            });

            // First-run consent: if not yet given, log a one-time notice. Full dialog is opt-in
            // via Tools menu; the deny-list still applies.
            if (!appSettings.Privacy.ConsentGiven)
            {
                _log.Info("First run: SQL text is stored locally under the storage root above. " +
                          "Configure server allow/deny lists by editing settings.json or via the Tools menu.");
                _settings.Mutate(s =>
                {
                    s.Privacy.ConsentGiven = true;
                    s.Privacy.ConsentTimestamp = DateTime.UtcNow.ToString("o");
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // Unsubscribe DTE event handlers BEFORE disposing dependents — otherwise a
                    // late OnWindowActivated firing into a half-torn-down package would NRE.
                    try { if (_windowEvents != null) _windowEvents.WindowActivated -= OnWindowActivated; } catch { }
                    try { if (_dteEvents != null) _dteEvents.OnBeginShutdown -= OnDteBeginShutdown; } catch { }
                    _windowEvents = null;
                    _dteEvents = null;

                    // Detach the WPF Quick Switcher hotkey hook before package teardown. If left attached
                    // when SSMS reloads the extension, the next instance would install a
                    // second handler and Quick Switcher would fire twice per keystroke.
                    try
                    {
                        if (_quickSwitchPreProcessHook != null)
                            System.Windows.Input.InputManager.Current.PreProcessInput -= _quickSwitchPreProcessHook;
                    }
                    catch { }
                    _quickSwitchPreProcessHook = null;

                    _pruneTimer?.Dispose();
                    _docTracker?.Dispose();
                    _store?.Dispose();
                }
                catch { }
            }
            base.Dispose(disposing);
        }

        private async Task<IVsOutputWindowPane> CreateOutputPaneAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow == null) return null;
            var paneGuid = OutputPaneGuid;
            ErrorHandler.ThrowOnFailure(outputWindow.CreatePane(ref paneGuid, OutputPaneTitle, fInitVisible: 1, fClearWithSolution: 0));
            ErrorHandler.ThrowOnFailure(outputWindow.GetPane(ref paneGuid, out var pane));
            return pane;
        }

        private async Task RegisterCommandsAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
            {
                Bind(mcs, PackageIds.ShowToolWindowCommandId,         OnShowToolWindowInvoked);
                Bind(mcs, PackageIds.SnapshotNowCommandId,            OnSnapshotNowInvoked);
                Bind(mcs, PackageIds.OpenSettingsCommandId,           OnOpenSettingsInvoked);
                Bind(mcs, PackageIds.QuickSwitcherCommandId,          OnQuickSwitcherInvoked);
                Bind(mcs, PackageIds.TagConfigCommandId,              OnTagConfigInvoked);
                Bind(mcs, PackageIds.NewViewCommandId,                OnNewViewInvoked);
            }
        }

        private void Bind(OleMenuCommandService mcs, int id, EventHandler handler)
        {
            mcs.AddCommand(new MenuCommand(handler, new CommandID(PackageGuids.AutoTabOrganiserCmdSet, id)));
        }

        // -------- command handlers --------

        private void OnShowToolWindowInvoked(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await ShowToolWindowAsync(typeof(TabOrganiserToolWindow), 0, true, DisposalToken);
                ApplyInstanceCaption(window as TabOrganiserToolWindow, 0);
                var control = (window as TabOrganiserToolWindow)?.Control;
                if (control != null) WireToolWindow(control);
            });
        }

        /// <summary>
        /// Open a NEW instance of the Tab Organiser tool window. Finds the lowest unused
        /// instance id (id=0 is the primary, restored across restarts; id=1..N are session-
        /// scoped). Each instance gets its own caption suffix so the user can tell them apart.
        /// </summary>
        private void OnNewViewInvoked(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                int id = FindNextFreeInstanceId();
                var window = await ShowToolWindowAsync(typeof(TabOrganiserToolWindow), id, true, DisposalToken);
                ApplyInstanceCaption(window as TabOrganiserToolWindow, id);
                var control = (window as TabOrganiserToolWindow)?.Control;
                if (control != null) WireToolWindow(control);
                _log?.Info($"Opened Tab Organiser instance #{id}.");
            });
        }

        /// <summary>
        /// Returns the lowest non-negative integer for which no <see cref="TabOrganiserToolWindow"/>
        /// frame currently exists. id=0 is the primary instance and is always probed first.
        /// </summary>
        private int FindNextFreeInstanceId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            for (int i = 0; i < 64; i++)
            {
                // create:false ⇒ probe only; returns null if no instance is registered at this id.
                if (FindToolWindow(typeof(TabOrganiserToolWindow), i, create: false) == null) return i;
            }
            // Safety net: 64 simultaneous instances is well past any reasonable use. Bail to a high
            // number rather than spinning indefinitely.
            return 64;
        }

        /// <summary>
        /// Suffix the caption with the instance index so multiple windows are distinguishable in
        /// the SSMS tab strip. id=0 stays as the unadorned title to keep the existing UX.
        /// </summary>
        private static void ApplyInstanceCaption(TabOrganiserToolWindow window, int id)
        {
            if (window == null) return;
            try { window.Caption = id == 0 ? "Tab Organiser" : $"Tab Organiser #{id + 1}"; }
            catch { }
        }

        private void OnSnapshotNowInvoked(object sender, EventArgs e)
        {
            _ = _docTracker?.ForceSnapshotActiveAsync("edit");
        }

        private void OnOpenSettingsInvoked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_settings.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex) { _log.Error("Open settings.json failed", ex); }
        }

        private void OnTagConfigInvoked(object sender, EventArgs e)
        {
            try
            {
                AutoTabOrganiser.UI.TagConfig.TagConfigWindow.Show(
                    _store, _settings, System.Windows.Application.Current?.MainWindow);
                _ = RefreshToolWindowAsync();
            }
            catch (Exception ex) { _log.Error("Tag config dialog failed", ex); }
        }

        /// <summary>
        /// Fires for every WPF input event before command routing. Matches the configured
        /// Quick Switcher chord (Ui.QuickSwitchHotkey) at the PreviewKeyDown stage and, when
        /// matched, marks the event handled and dispatches the Quick Switcher. Covers the
        /// non-editor scope; the in-editor scope is the MEF QuickSwitchKeyProcessor.
        /// </summary>
        private void OnPreProcessInputForQuickSwitch(object sender, System.Windows.Input.PreProcessInputEventArgs e)
        {
            try
            {
                if (_quickSwitchKey == System.Windows.Input.Key.None) return;
                var input = e?.StagingItem?.Input;
                if (input == null) return;
                if (!(input is System.Windows.Input.KeyEventArgs k)) return;
                // PreviewKeyDown is the tunneling phase — we want to act before anything
                // bubbles or the editor's command target sees it. The KeyDown bubbling
                // phase fires for the same physical event afterwards; we don't need it
                // because cancelling at PreProcess suppresses both phases.
                if (k.RoutedEvent != System.Windows.Input.Keyboard.PreviewKeyDownEvent) return;
                if (k.Key != _quickSwitchKey) return;
                const System.Windows.Input.ModifierKeys mask =
                    System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift |
                    System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Windows;
                if ((System.Windows.Input.Keyboard.Modifiers & mask) != _quickSwitchModifiers) return;

                k.Handled = true;
                e.Cancel();
                OnQuickSwitcherInvoked(this, EventArgs.Empty);
            }
            catch (Exception ex) { _log?.Debug("Quick Switcher hotkey hook failed: " + ex.Message); }
        }

        private void OnQuickSwitcherInvoked(object sender, EventArgs e)
        {
            if (_store == null) return;
            try
            {
                // Capture the active SSMS document's connection BEFORE the popup steals focus.
                // After we open the picked tab, the active doc is the new (unconnected) tab,
                // so the title-parse trick doesn't work post-open.
                var preActiveConnection = TryReadActiveConnection();

                AutoTabOrganiser.UI.QuickSwitcher.QuickSwitcherWindow.Show(
                    _store, _settings, _log,
                    openTabAtText: (tabId, findText) => OpenTabAtTextAsync(tabId, findText, preActiveConnection),
                    owner: System.Windows.Application.Current?.MainWindow,
                    // Lets the switcher pre-select the *previous* tab (alt-tab reflex) —
                    // it needs to know which row is the one the user is already sitting in.
                    currentTabId: _docTracker?.GetActiveTabId());
            }
            catch (Exception ex) { _log.Error("QuickSwitcher show failed", ex); }
        }

        /// <summary>
        /// Best-effort read of the currently-active SSMS document's connection (server, database)
        /// by parsing the shell window caption. Null fields mean no detectable connection.
        /// </summary>
        private (string server, string database) TryReadActiveConnection()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = (EnvDTE.DTE)GetService(typeof(EnvDTE.DTE));
                var caption = dte?.MainWindow?.Caption;
                return Tracking.ConnectionExtractor.FromWindowTitle(caption);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Open a tab (existing logic), then if <paramref name="findText"/> is non-empty,
        /// move the editor's selection to the first occurrence so the user lands on what
        /// they searched for. If <paramref name="preActiveConnection"/> contains a server
        /// name (i.e. there *was* a current connection at the moment Ctrl+P was invoked),
        /// pop SSMS's Connect dialog so the user can reattach with one click.
        /// </summary>
        private async Task OpenTabAtTextAsync(string tabId, string findText,
                                              (string server, string database) preActiveConnection)
        {
            var switchedToExisting = await OpenTabFromHistoryAsync(tabId);

            // Find-text first so the cursor is positioned even if the connect step throws.
            if (!string.IsNullOrEmpty(findText))
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                    var doc = dte?.ActiveDocument;
                    if (doc?.Selection is EnvDTE.TextSelection sel)
                    {
                        sel.StartOfDocument();
                        sel.FindText(findText, 0);
                    }
                }
                catch (Exception ex) { _log?.Debug("Navigate-to-text failed: " + ex.Message); }
            }

            // Auto-connect: only prompt when the previously-active doc had a connection.
            // SSMS's Query.Connection command opens the Connect dialog pre-populated with
            // the last-used connection (typically the one we want); user clicks Connect.
            // Skipped when we merely switched to an already-open tab — that tab keeps
            // whatever connection it already has; popping the dialog would just be noise.
            if (!switchedToExisting && !string.IsNullOrEmpty(preActiveConnection.server))
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                    dte?.ExecuteCommand("Query.Connection");
                }
                catch (Exception ex) { _log?.Debug("Auto-connect prompt failed: " + ex.Message); }
            }
        }

        // -------- tool window wiring --------

        private void WireToolWindow(ToolWindowControl control)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (control == null) return;
            var s = _settings.Load();
            control.Initialise(_store,
                openTabId: tabId => OpenTabFromHistoryAsync(tabId),
                onSettingsClick: () => OnOpenSettingsInvoked(this, EventArgs.Empty),
                onSnapshotNow:   () => OnSnapshotNowInvoked(this, EventArgs.Empty),
                onQuickSwitcher: () => OnQuickSwitcherInvoked(this, EventArgs.Empty),
                onTagConfig:     () => OnTagConfigInvoked(this, EventArgs.Empty),
                onNewView:       () => OnNewViewInvoked(this, EventArgs.Empty),
                log: _log,
                settings: _settings,
                sortMode: s.Ui.TabsSortMode);
            control.OpenSnapshotHandler = OpenSnapshotInNewTabAsync;
            control.RestoreSnapshotHandler = RestoreSnapshotAsNewTabAsync;

            // Seed the active-tab id so the row is pinned even before any focus change.
            try { control.SetActiveTabId(_docTracker?.GetActiveTabId()); } catch { }

            // Offer session restore on the first tool window only — secondary "New View"
            // instances share the same store and don't need a second banner.
            if (!_sessionRestoreOffered && _previousSessionTabs != null && _previousSessionTabs.Count > 0)
            {
                _sessionRestoreOffered = true;
                var count = _previousSessionTabs.Count;
                control.OfferSessionRestore(count, () => _ = JoinableTaskFactory.RunAsync(ReopenPreviousSessionAsync));
            }
        }

        /// <summary>
        /// Reopen every tab the previous session had open, newest first. Each open goes
        /// through <see cref="OpenTabFromHistoryAsync"/>, which prefers the saved on-disk
        /// script and falls back to materialising the latest snapshot — so unsaved content
        /// lost to a crash comes back too. Capped so a pathological session can't open a
        /// hundred windows.
        /// </summary>
        private async Task ReopenPreviousSessionAsync()
        {
            var tabs = _previousSessionTabs;
            _previousSessionTabs = null;
            if (tabs == null) return;

            const int maxReopen = 30;
            int opened = 0;
            foreach (var t in tabs)
            {
                if (opened >= maxReopen)
                {
                    _log?.Info($"Session restore: stopped at {maxReopen} tabs ({tabs.Count - opened} not reopened).");
                    break;
                }
                try
                {
                    await OpenTabFromHistoryAsync(t.TabId);
                    opened++;
                }
                catch (Exception ex) { _log?.Warn($"Session restore: reopen failed for {t.TabId}: {ex.Message}"); }
            }
            _log?.Info($"Session restore: reopened {opened} tab(s).");
        }

        /// <summary>
        /// Open an old snapshot's content as a brand-new query tab. Restore never touches the
        /// original tab: the content gets a freshly-generated @id (the old trailing @id is
        /// stripped by SetId) and a "(restored …)" name, so the copy snapshots under its own
        /// identity from the first poll.
        /// </summary>
        internal async Task RestoreSnapshotAsNewTabAsync(SnapshotRecord r)
        {
            if (r == null) return;
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var content = _store.ReadSnapshotContentById(r.Id) ?? string.Empty;

                var newId = MetadataWriter.GenerateTabId();
                var stamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                var baseName = string.IsNullOrEmpty(r.Name) ? "snapshot" : r.Name;
                var restoredName = baseName + " (restored " + stamp + ")";

                content = MetadataWriter.SetName(content, restoredName);
                content = MetadataWriter.SetId(content, newId);

                var openDir = Path.Combine(_store.Root, "open");
                Directory.CreateDirectory(openDir);
                var tmp = Path.Combine(openDir, $"{SafeForFile(restoredName)} [{newId}].sql");
                File.WriteAllText(tmp, content);

                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                dte?.ItemOperations?.OpenFile(tmp);
                _log?.Info($"Restored snapshot {r.Id} (tab {r.TabId}, {stamp}) as new tab {newId}.");
            }
            catch (Exception ex) { _log.Error("Restore snapshot as new tab failed", ex); }
        }

        /// <summary>
        /// Called from <see cref="TabOrganiserToolWindow.OnToolWindowCreated"/>. The store may not
        /// have finished initialising yet on cold restore — in that case we retry shortly.
        /// </summary>
        internal void WireToolWindowSafe(TabOrganiserToolWindow window)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_store != null && window.Control != null)
                    {
                        WireToolWindow(window.Control);
                        return;
                    }
                    await Task.Delay(250);
                }
                // Loud-failure path: if 20 × 250ms (~5s) wasn't enough for the store/control to
                // appear, the tool window's commands will silently no-op. Log so we can diagnose
                // it from the extension log rather than guessing why buttons don't respond.
                _log?.Error($"WireToolWindowSafe gave up after 5s — store={(_store == null ? "null" : "ok")}, " +
                            $"control={(window?.Control == null ? "null" : "ok")}. Tool window commands will not function.");
            });
        }

        /// <summary>
        /// Opens the tab's content in the editor. Returns true when the tab was ALREADY open
        /// — in that case its existing window is activated instead of materialising a second
        /// copy of the same query (which would duplicate the TabId across two documents).
        /// </summary>
        private async Task<bool> OpenTabFromHistoryAsync(string tabId)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                if (_docTracker != null && _docTracker.TryActivateTab(tabId))
                {
                    _log?.Info($"Open request for {tabId}: already open — switched to the existing tab.");
                    return true;
                }
            }
            catch (Exception ex) { _log?.Debug("Activate-existing-tab check failed: " + ex.Message); }

            // If this tab has ever been "Save to scripts"-d, open the on-disk script directly
            // so subsequent edits and Ctrl+S go to the canonical file rather than a scratch
            // copy. Otherwise fall back to materialising the latest snapshot into the open/
            // staging folder.
            try
            {
                var saved = _store.ListSnapshots("tab_id=$t AND reason=$r",
                    new[]
                    {
                        new System.Collections.Generic.KeyValuePair<string, object>("$t", tabId),
                        new System.Collections.Generic.KeyValuePair<string, object>("$r", "saved"),
                    }, 1);
                if (saved.Count > 0
                    && !string.IsNullOrEmpty(saved[0].FilePath)
                    && File.Exists(saved[0].FilePath))
                {
                    var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                    dte?.ItemOperations?.OpenFile(saved[0].FilePath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Open saved-script path failed; falling back to snapshot copy", ex);
            }

            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new System.Collections.Generic.KeyValuePair<string, object>("$t", tabId) }, 1);
            if (snaps.Count == 0) return false;
            await OpenSnapshotInNewTabAsync(snaps[0]);
            return false;
        }

        private static string SafeForFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "snapshot";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var clean = sb.ToString().Trim();
            if (clean.Length > 60) clean = clean.Substring(0, 60).Trim();
            return string.IsNullOrEmpty(clean) ? "snapshot" : clean;
        }

        private async Task OpenSnapshotInNewTabAsync(SnapshotRecord r)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var openDir = Path.Combine(_store.Root, "open");
                Directory.CreateDirectory(openDir);
                var safeName = SafeForFile(r.Name ?? "snapshot");
                // Suffix the title with [tabId] so the SSMS tab caption shows the stable identity.
                var tmp = Path.Combine(openDir, $"{safeName} [{r.TabId}].sql");

                // Before writing, remove any other file in the open/ folder that maps to the
                // same TabId. The tab's @name can change between opens — if it does, the old
                // filename would otherwise persist as an orphan forever. We don't delete `tmp`
                // itself; File.WriteAllText below will overwrite that one in place.
                try
                {
                    foreach (var stale in Directory.EnumerateFiles(openDir, "*[" + r.TabId + "].sql"))
                    {
                        if (string.Equals(stale, tmp, StringComparison.OrdinalIgnoreCase)) continue;
                        try { File.Delete(stale); }
                        catch (Exception ex) { _log?.Debug($"open/ orphan cleanup skip {stale}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { _log?.Debug("open/ orphan enumeration failed: " + ex.Message); }

                File.WriteAllText(tmp, _store.ReadSnapshotContentById(r.Id));

                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                dte?.ItemOperations?.OpenFile(tmp);
            }
            catch (Exception ex) { _log.Error("Open snapshot failed", ex); }
        }

        /// <summary>
        /// Walks the user's saved-scripts folder and imports any .sql file that isn't already
        /// represented in the DB as a snapshot row with <c>reason='saved'</c> and a matching
        /// <c>file_path</c>. For each new file we synthesise a snapshot row so the file shows up
        /// in the Tab Organiser sidebar exactly as if the user had saved it from SSMS originally.
        ///
        /// Idempotency: the tab_id we assign is <c>meta.Id</c> when present in the file's header,
        /// otherwise a deterministic GUID derived from the absolute path. So re-runs of this
        /// scan don't create duplicates even if the file's @id is missing.
        /// </summary>
        /// <summary>
        /// Snapshot of full paths currently open as SSMS documents. Returned on the UI thread
        /// at startup before the saved-scripts import begins; the import then refuses to write
        /// back to any of these paths, preventing a "file changed outside the editor" reload
        /// prompt from clobbering unsaved edits.
        /// </summary>
        private HashSet<string> TryEnumerateOpenDocumentPaths()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dte = (EnvDTE.DTE)GetService(typeof(EnvDTE.DTE));
                var docs = dte?.Documents;
                if (docs == null) return set;
                foreach (EnvDTE.Document d in docs)
                {
                    try
                    {
                        var full = d?.FullName;
                        if (!string.IsNullOrEmpty(full)) set.Add(Path.GetFullPath(full));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { _log?.Debug("Enumerate open documents failed: " + ex.Message); }
            return set;
        }

        private void ImportSavedScriptsFolder(AppSettings settings, HashSet<string> openDocPaths)
        {
            var folder = settings?.SavedScripts?.FolderPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            openDocPaths = openDocPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Build a set of paths already known to the DB so we don't re-import on every start.
            // ListSnapshots with WHERE reason='saved' returns one row per save; we project to
            // the (case-insensitive) full path set.
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var saved = _store.ListSnapshots("reason=$r",
                    new[] { new System.Collections.Generic.KeyValuePair<string, object>("$r", "saved") },
                    int.MaxValue);
                foreach (var s in saved)
                    if (!string.IsNullOrEmpty(s.FilePath)) known.Add(Path.GetFullPath(s.FilePath));
            }
            catch (Exception ex) { _log?.Debug("ImportSavedScriptsFolder: ListSnapshots failed: " + ex.Message); }

            int imported = 0;
            int scanned  = 0;
            foreach (var path in EnumerateSqlFilesSafe(folder))
            {
                scanned++;
                try
                {
                    var full = Path.GetFullPath(path);
                    if (known.Contains(full)) continue;

                    string content;
                    try { content = File.ReadAllText(full); }
                    catch (Exception ex) { _log?.Debug($"ImportSavedScriptsFolder skip {full}: {ex.Message}"); continue; }

                    var meta = MetadataParser.Parse(content);
                    string tabId;
                    if (!string.IsNullOrWhiteSpace(meta.Id))
                    {
                        tabId = meta.Id;
                    }
                    else
                    {
                        // No @id in the file. Generate a deterministic id from the path AND write
                        // it into the file so a later SSMS open reuses the same id (instead of
                        // SSMS's AutoIdInjector minting a fresh one and creating a duplicate
                        // tabs_latest row). If the file write fails (read-only, locked, etc.),
                        // import anyway with the deterministic id — duplication on a later open
                        // is the worse failure, but losing the import is also bad.
                        var deterministic = DeterministicGuidFromPath(full).ToString();
                        tabId = deterministic;
                        // Data safety: if SSMS already has the file open, do NOT rewrite it
                        // here. The reload-prompt could discard the user's unsaved edits. The
                        // import still proceeds with the deterministic id; if a later session
                        // opens the file the AutoTagger can inject the id then.
                        if (openDocPaths.Contains(full))
                        {
                            _log?.Debug($"ImportSavedScriptsFolder: @id write skipped (open in SSMS): {full}");
                        }
                        else
                        {
                            try
                            {
                                var withId = MetadataWriter.SetId(content, deterministic);
                                if (!ReferenceEquals(withId, content) && withId != content)
                                {
                                    File.WriteAllText(full, withId);
                                    content = withId;
                                }
                            }
                            catch (Exception ex)
                            {
                                _log?.Debug($"ImportSavedScriptsFolder: @id write skipped for {full}: {ex.Message}");
                            }
                        }
                    }

                    // Capture mtime AFTER any @id injection so we use the disk-truth timestamp.
                    var nowMs = ((DateTimeOffset)File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
                    var name  = meta.Name ?? Path.GetFileNameWithoutExtension(full);

                    var record = new SnapshotRecord
                    {
                        Id = Guid.NewGuid().ToString(),
                        TabId = tabId,
                        FilePath = full,
                        Folder = meta.Folder,
                        Name = name,
                        ContentHash = Hashing.Sha256Hex(content),
                        Reason = "saved",
                        Ts = nowMs,
                        Server = meta.Server,
                        Database = meta.Database,
                        Tags = meta.Tags ?? new System.Collections.Generic.List<string>(),
                        Desc = meta.Description,
                    };
                    _store.WriteSnapshot(record, content);
                    known.Add(full);
                    imported++;
                }
                catch (Exception ex)
                {
                    _log?.Warn($"ImportSavedScriptsFolder error on {path}: {ex.Message}");
                }
            }

            if (imported > 0)
            {
                _log?.Info($"Saved-scripts import: scanned={scanned}, imported={imported} (folder='{folder}').");
                // Tell the sidebar (any open instance) to refresh so the imports are visible.
                _ = RefreshToolWindowAsync();
            }
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateSqlFilesSafe(string folder)
        {
            // Manual recursion so a single inaccessible subdirectory (perms) doesn't abort
            // the whole walk. Reparse points (junctions/symlinks) are skipped — following
            // one can loop forever — and a depth cap backstops anything else pathological.
            var stack = new System.Collections.Generic.Stack<(string dir, int depth)>();
            stack.Push((folder, 0));
            const int maxDepth = 32;
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                System.Collections.Generic.IEnumerable<string> subs = Array.Empty<string>();
                System.Collections.Generic.IEnumerable<string> files = Array.Empty<string>();
                try { subs  = Directory.EnumerateDirectories(dir); } catch { }
                try { files = Directory.EnumerateFiles(dir, "*.sql"); } catch { }
                foreach (var f in files) yield return f;
                if (depth >= maxDepth) continue;
                foreach (var s in subs)
                {
                    bool reparse = false;
                    try { reparse = (File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0; } catch { }
                    if (!reparse) stack.Push((s, depth + 1));
                }
            }
        }

        /// <summary>
        /// Path → stable GUID. Same path always produces the same GUID, so re-running the
        /// import scan never creates a duplicate tab_id row.
        /// </summary>
        private static Guid DeterministicGuidFromPath(string path)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path ?? string.Empty));
                var g = new byte[16];
                Array.Copy(bytes, g, 16);
                return new Guid(g);
            }
        }

        /// <summary>
        /// On startup, delete files in <c>open/</c> older than <paramref name="maxAgeDays"/>.
        /// Files still locked by SSMS (because they're currently open in a tab) throw
        /// <see cref="IOException"/> on <see cref="File.Delete"/>; we swallow those — the file
        /// is by definition still in use and will be cleaned next session if no longer needed.
        /// </summary>
        private void PruneStaleOpenFiles(int maxAgeDays = 30)
        {
            try
            {
                var openDir = Path.Combine(_store.Root, "open");
                if (!Directory.Exists(openDir)) return;
                var threshold = DateTime.UtcNow.AddDays(-maxAgeDays);
                int deleted = 0;
                foreach (var f in Directory.EnumerateFiles(openDir, "*.sql"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) >= threshold) continue;
                        File.Delete(f);
                        deleted++;
                    }
                    catch (Exception ex) { _log?.Debug($"open/ prune skip {f}: {ex.Message}"); }
                }
                if (deleted > 0) _log?.Info($"open/ prune: deleted {deleted} stale file(s) older than {maxAgeDays}d.");
            }
            catch (Exception ex) { _log?.Debug("PruneStaleOpenFiles failed: " + ex.Message); }
        }

        private void OnDteBeginShutdown() => IsShuttingDown = true;

        private string _lastTouchedTabId;

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var tabId = _docTracker?.GetActiveTabId();
                // Multi-instance: every open tool window needs its active-tab pin updated,
                // not just the primary (id=0) one.
                ForEachToolWindow(w => w?.Control?.SetActiveTabId(tabId));

                // MRU signal for the Quick Switcher. Activation fires for every focus bounce
                // (tool window ↔ editor), so only touch when the active document actually
                // changed; the write goes off-thread to keep SQLite IO off the UI thread.
                if (!string.IsNullOrEmpty(tabId) && tabId != _lastTouchedTabId)
                {
                    _lastTouchedTabId = tabId;
                    var store = _store;
                    if (store != null)
                        _ = Task.Run(() =>
                        {
                            try { store.TouchActivated(tabId); }
                            catch (Exception ex) { _log?.Debug("TouchActivated failed: " + ex.Message); }
                        });
                }
            }
            catch (Exception ex) { _log?.Debug("WindowActivated handler failed: " + ex.Message); }
        }

        private Task RefreshToolWindowAsync()
        {
            return JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ForEachToolWindow(w => w?.Control?.RefreshTabs());
            }).Task;
        }

        /// <summary>
        /// Invokes <paramref name="action"/> for every currently-live <see cref="TabOrganiserToolWindow"/>
        /// instance, primary (id=0) and any session-scoped secondaries opened via "New View".
        /// Must be called on the UI thread.
        /// </summary>
        private void ForEachToolWindow(Action<TabOrganiserToolWindow> action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The id space is sparse but small; probe a fixed range. FindToolWindow with
            // create:false returns null when the id is unused, so we just skip those.
            for (int i = 0; i < 64; i++)
            {
                TabOrganiserToolWindow w = null;
                try { w = FindToolWindow(typeof(TabOrganiserToolWindow), i, create: false) as TabOrganiserToolWindow; }
                catch { /* unregistered id — skip */ }
                if (w != null) action(w);
            }
        }

        private void ScheduleDailyPrune(AppSettings appSettings)
        {
            _pruner = new Pruner(_store, _log, ((long)appSettings.Storage.MaxStorageMB) * 1024 * 1024);

            // Run once on load (in 30s).
            _pruneTimer = new Timer(_ =>
            {
                try
                {
                    var s = _settings.Load();
                    if (!s.Storage.RetentionEnabled) return;
                    var result = _pruner.Prune(DateTime.UtcNow);
                    _log.Info(result.ToString());
                }
                catch (Exception ex) { _log.Error("Prune failed", ex); }
            }, null, dueTime: TimeSpan.FromSeconds(30), period: TimeSpan.FromHours(24));
        }
    }
}
