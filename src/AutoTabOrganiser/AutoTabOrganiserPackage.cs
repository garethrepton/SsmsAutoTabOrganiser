using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
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
    [ProvideToolWindow(typeof(TabOrganiserToolWindow), Style = VsDockStyle.Tabbed, Window = "3AE79031-E1BC-11D0-8F78-00A0C9110057")]
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

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _pane = await CreateOutputPaneAsync(cancellationToken);

            _settings = new SettingsStore(SettingsStore.DefaultSettingsFilePath());
            var appSettings = _settings.Load();

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
                cancellationToken);

            // Subscribe to SSMS window-activation so we can pin the user's currently-focused
            // SQL tab to the top of the RECENT section. DTE.Events references must be held
            // (not GC'd) for the events to keep firing.
            try
            {
                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                if (dte != null)
                {
                    _windowEvents = dte.Events.WindowEvents;
                    _windowEvents.WindowActivated += OnWindowActivated;
                }
            }
            catch (Exception ex) { _log.Debug("WindowEvents subscribe failed: " + ex.Message); }

            ScheduleDailyPrune(appSettings);

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
                Bind(mcs, PackageIds.HelloCommandId,                  OnHelloInvoked);
                Bind(mcs, PackageIds.ShowToolWindowCommandId,         OnShowToolWindowInvoked);
                Bind(mcs, PackageIds.SnapshotNowCommandId,            OnSnapshotNowInvoked);
                Bind(mcs, PackageIds.OpenSettingsCommandId,           OnOpenSettingsInvoked);
                Bind(mcs, PackageIds.QuickSwitcherCommandId,          OnQuickSwitcherInvoked);
                Bind(mcs, PackageIds.TagColoursCommandId,             OnTagColoursInvoked);
            }
        }

        private void Bind(OleMenuCommandService mcs, int id, EventHandler handler)
        {
            mcs.AddCommand(new MenuCommand(handler, new CommandID(PackageGuids.AutoTabOrganiserCmdSet, id)));
        }

        // -------- command handlers --------

        private void OnHelloInvoked(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(this, "Hello from Auto Tab Organiser.", "Tab History",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private void OnShowToolWindowInvoked(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await ShowToolWindowAsync(typeof(TabOrganiserToolWindow), 0, true, DisposalToken);
                var control = (window as TabOrganiserToolWindow)?.Control;
                if (control != null) WireToolWindow(control);
            });
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

        private void OnTagColoursInvoked(object sender, EventArgs e)
        {
            try
            {
                AutoTabOrganiser.UI.TagColours.TagColoursWindow.Show(
                    _store, _settings, System.Windows.Application.Current?.MainWindow);
                _ = RefreshToolWindowAsync();
            }
            catch (Exception ex) { _log.Error("Tag colours dialog failed", ex); }
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
                    owner: System.Windows.Application.Current?.MainWindow);
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
            await OpenTabFromHistoryAsync(tabId);

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
            if (!string.IsNullOrEmpty(preActiveConnection.server))
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
                log: _log,
                settings: _settings,
                viewMode: s.Ui.TabsViewMode,
                sortMode: s.Ui.TabsSortMode);
            control.OpenSnapshotHandler = OpenSnapshotInNewTabAsync;

            // Seed the active-tab id so the row is pinned even before any focus change.
            try { control.SetActiveTabId(_docTracker?.GetActiveTabId()); } catch { }
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
            });
        }

        private async Task OpenTabFromHistoryAsync(string tabId)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

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
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Open saved-script path failed; falling back to snapshot copy", ex);
            }

            var snaps = _store.ListSnapshots("tab_id=$t",
                new[] { new System.Collections.Generic.KeyValuePair<string, object>("$t", tabId) }, 1);
            if (snaps.Count == 0) return;
            await OpenSnapshotInNewTabAsync(snaps[0]);
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
                File.WriteAllText(tmp, _store.ReadSnapshotContentById(r.Id));

                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
                dte?.ItemOperations?.OpenFile(tmp);
            }
            catch (Exception ex) { _log.Error("Open snapshot failed", ex); }
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var tabId = _docTracker?.GetActiveTabId();
                var window = FindToolWindow(typeof(TabOrganiserToolWindow), 0, false) as TabOrganiserToolWindow;
                window?.Control?.SetActiveTabId(tabId);
            }
            catch (Exception ex) { _log?.Debug("WindowActivated handler failed: " + ex.Message); }
        }

        private Task RefreshToolWindowAsync()
        {
            return JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = FindToolWindow(typeof(TabOrganiserToolWindow), 0, false) as TabOrganiserToolWindow;
                window?.Control?.RefreshTabs();
            }).Task;
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
