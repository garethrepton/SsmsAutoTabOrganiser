using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;
using Task = System.Threading.Tasks.Task;

namespace AutoTabOrganiser.Tracking
{
    /// <summary>
    /// Tracks open SQL documents in the running document table and drives a polling loop
    /// that reads each document's text, offers it to its SnapshotPipeline, and triggers
    /// autosave for documents that have a real file path. Polling (rather than ITextBuffer
    /// event subscription) is robust against SSMS's custom T-SQL editor.
    /// </summary>
    internal sealed class DocumentTracker : IVsRunningDocTableEvents3, IDisposable
    {
        private readonly IServiceProvider _sp;
        private readonly IVsEditorAdaptersFactoryService _adapterFactory;
        private readonly RunningDocumentTable _rdt;
        private readonly SnapshotStore _store;
        private readonly SettingsStore _settings;
        private readonly Logger _log;
        private readonly Action<string> _onTabUpdated;
        private readonly Action<string> _onTabClosed;
        private readonly Dictionary<uint, SnapshotPipeline> _pipelines = new Dictionary<uint, SnapshotPipeline>();
        private uint _cookie;
        private Timer _pollTimer;

        private DocumentTracker(IServiceProvider sp, IVsEditorAdaptersFactoryService adapterFactory,
                                SnapshotStore store, SettingsStore settings, Logger log,
                                Action<string> onTabUpdated, Action<string> onTabClosed)
        {
            _sp = sp;
            _adapterFactory = adapterFactory;
            _rdt = new RunningDocumentTable(sp);
            _store = store;
            _settings = settings;
            _log = log;
            _onTabUpdated = onTabUpdated;
            _onTabClosed = onTabClosed;
        }

        public static async Task<DocumentTracker> CreateAsync(IAsyncServiceProvider asp,
                                                              IVsEditorAdaptersFactoryService adapterFactory,
                                                              SnapshotStore store,
                                                              SettingsStore settings,
                                                              Logger log,
                                                              Action<string> onTabUpdated,
                                                              Action<string> onTabClosed,
                                                              CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var t = new DocumentTracker((IServiceProvider)asp, adapterFactory, store, settings, log, onTabUpdated, onTabClosed);
            t._cookie = t._rdt.Advise(t);
            log.Info("DocumentTracker: subscribed to RDT.");

            foreach (var info in t._rdt) t.AttachIfSqlDoc(info.DocCookie, info.Moniker);

            // 500ms keeps detection latency under a second (a fresh tab appears in the side
            // panel within ~half a second of the user typing) without measurably bumping CPU.
            // Each tick is a hash-compare per pipeline and an early-out on no-change.
            t._pollTimer = new Timer(_ => t.PollOnce(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            return t;
        }

        public void Dispose()
        {
            try { _pollTimer?.Dispose(); } catch { }
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_cookie != 0)
                {
                    try { _rdt.Unadvise(_cookie); } catch { }
                    _cookie = 0;
                }
                foreach (var p in _pipelines.Values) p.Dispose();
                _pipelines.Clear();
            });
        }

        public Task ForceSnapshotActiveAsync(string reason)
        {
            return ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var cookie = GetActiveDocCookie();
                if (cookie == 0) return;
                SnapshotPipeline pipeline;
                lock (_pipelines) _pipelines.TryGetValue(cookie, out pipeline);
                if (pipeline == null) return;
                var text = ReadDocText(cookie);
                if (text != null) pipeline.OfferSnapshot(text, reason ?? "edit");
            }).Task;
        }

        // ---------- IVsRunningDocTableEvents ----------

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dwReadLocksRemaining + dwEditLocksRemaining == 1)
            {
                var info = _rdt.GetDocumentInfo(docCookie);
                AttachIfSqlDoc(docCookie, info.Moniker);
            }
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SnapshotPipeline pipeline;
            lock (_pipelines) _pipelines.TryGetValue(docCookie, out pipeline);
            if (pipeline != null)
            {
                var text = ReadDocText(docCookie);
                pipeline.OnSaved(text);
            }
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dwReadLocksRemaining + dwEditLocksRemaining == 0) DetachIfTracked(docCookie);
            return VSConstants.S_OK;
        }

        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)         => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnBeforeSave(uint docCookie)                                     => VSConstants.S_OK;

        // ---------- attach / detach ----------

        private void AttachIfSqlDoc(uint docCookie, string moniker)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsLikelySqlDoc(docCookie, moniker)) return;

            lock (_pipelines)
            {
                if (_pipelines.ContainsKey(docCookie)) return;
            }

            Func<string> getTitle = () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { var dte = (EnvDTE.DTE)_sp.GetService(typeof(EnvDTE.DTE)); return dte?.MainWindow?.Caption; }
                catch { return null; }
            };

            var pipeline = new SnapshotPipeline(
                moniker, getTitle, _store, _settings, _log,
                _onTabUpdated, _onTabClosed,
                tryInjectId: (id, mk, meta) => InjectIdAtBufferOnUiThread(docCookie, id, meta),
                tryInjectAutoTags: tags => InjectAutoTagsAtBufferOnUiThread(docCookie, tags));

            lock (_pipelines) _pipelines[docCookie] = pipeline;
            _log.Info($"[doc opened] {SafeName(moniker)}  [{moniker}]");

            // Fire an immediate "first" snapshot so the side panel shows the tab right away
            // instead of waiting for the edit-debounce window. Empty content is skipped inside
            // the pipeline so a blank untitled tab doesn't clutter the panel.
            try
            {
                var firstText = ReadDocText(docCookie);
                if (firstText != null) pipeline.OfferSnapshot(firstText, "first");
            }
            catch (Exception ex) { _log.Debug($"first-snapshot failed for {moniker}: {ex.Message}"); }
        }

        private void DetachIfTracked(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SnapshotPipeline pipeline;
            string moniker = null;
            try { moniker = _rdt.GetDocumentInfo(docCookie).Moniker; } catch { }

            lock (_pipelines)
            {
                if (!_pipelines.TryGetValue(docCookie, out pipeline)) return;
                _pipelines.Remove(docCookie);
            }
            try
            {
                var text = ReadDocText(docCookie);
                pipeline.OnClosed(text);
                _log.Info($"[doc closed] {SafeName(moniker ?? pipeline.Moniker)}");

                // Mark the tab as closed in the index so the quick switcher sorts it after
                // currently-open tabs. WriteSnapshot always sets is_open=1; we have to flip
                // it back here.
                if (!string.IsNullOrEmpty(pipeline.TabId))
                {
                    try { _store.SetTabState(pipeline.TabId, isOpen: false, isDirty: null); }
                    catch (Exception ex) { _log.Debug("SetTabState(open=false) failed: " + ex.Message); }
                }
            }
            finally { pipeline.Dispose(); }
        }

        private bool IsLikelySqlDoc(uint docCookie, string moniker)
        {
            // Accept by name first.
            if (!string.IsNullOrEmpty(moniker))
            {
                if (moniker.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return true;
                if (moniker.IndexOf("SQLQuery", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            // Accept by editor: any document whose DocData exposes IVsTextLines AND looks like a
            // SSMS query window (best-effort: we just accept all text-based docs; the metadata
            // parser is forgiving for non-SQL content but the side-panel only shows SQL-shaped
            // tabs anyway).
            try
            {
                var info = _rdt.GetDocumentInfo(docCookie);
                if (info.DocData is IVsTextLines) return true;
                if (info.DocData is IVsTextBufferProvider) return true;
            }
            catch { }
            return false;
        }

        // ---------- text reading ----------

        private string ReadDocText(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var info = _rdt.GetDocumentInfo(docCookie);
                var text = ReadFromDocData(info.DocData);
                if (text != null) return text;
                // Last resort: read the file from disk if it exists.
                if (!string.IsNullOrEmpty(info.Moniker) && Path.IsPathRooted(info.Moniker) && File.Exists(info.Moniker))
                    return File.ReadAllText(info.Moniker);
            }
            catch (Exception ex) { _log.Debug($"ReadDocText failed for {docCookie}: {ex.Message}"); }
            return null;
        }

        private string ReadFromDocData(object docData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                IVsTextLines lines = docData as IVsTextLines;
                if (lines == null && docData is IVsTextBufferProvider prov)
                {
                    if (prov.GetTextBuffer(out lines) != VSConstants.S_OK) lines = null;
                }
                if (lines != null)
                {
                    if (lines.GetLastLineIndex(out int lastLine, out int lastIndex) == VSConstants.S_OK
                        && lines.GetLineText(0, 0, lastLine, lastIndex, out string text) == VSConstants.S_OK)
                    {
                        return text;
                    }
                }

                // Plain ITextBuffer route (kept as a backup).
                if (docData is IVsTextBuffer vsBuf && _adapterFactory != null)
                {
                    var buf = _adapterFactory.GetDocumentBuffer(vsBuf) ?? _adapterFactory.GetDataBuffer(vsBuf);
                    if (buf != null) return buf.CurrentSnapshot.GetText();
                }
            }
            catch (Exception ex) { _log.Debug($"ReadFromDocData failed: {ex.Message}"); }
            return null;
        }

        private bool InjectIdAtBufferOnUiThread(uint docCookie, string id, ParsedMetadata meta)
        {
            // Best-effort: try the ITextBuffer route. Skip silently if unavailable; @id isn't
            // critical (fingerprint-based identity still works).
            try
            {
                bool ok = false;
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var info = _rdt.GetDocumentInfo(docCookie);
                    if (!(info.DocData is IVsTextBuffer vsBuf) || _adapterFactory == null) return;
                    var buffer = _adapterFactory.GetDocumentBuffer(vsBuf) ?? _adapterFactory.GetDataBuffer(vsBuf);
                    if (buffer == null) return;
                    var text = buffer.CurrentSnapshot.GetText();
                    var fresh = MetadataParser.Parse(text);
                    if (!string.IsNullOrEmpty(fresh.Id) || fresh.CommentBlockEndExclusive == 0) return;
                    var newText = MetadataWriter.InjectId(text, id, fresh.CommentBlockEndExclusive);
                    if (newText == null) return;
                    using (var edit = buffer.CreateEdit())
                    {
                        var insertion = newText.Substring(fresh.CommentBlockEndExclusive,
                            newText.Length - text.Length);
                        edit.Insert(fresh.CommentBlockEndExclusive, insertion);
                        edit.Apply();
                    }
                    ok = true;
                });
                return ok;
            }
            catch (Exception ex) { _log.Error("@id injection failed", ex); return false; }
        }

        private bool InjectAutoTagsAtBufferOnUiThread(uint docCookie, List<string> tags)
        {
            if (tags == null || tags.Count == 0) return false;
            try
            {
                bool ok = false;
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var info = _rdt.GetDocumentInfo(docCookie);
                    if (!(info.DocData is IVsTextBuffer vsBuf) || _adapterFactory == null) return;
                    var buffer = _adapterFactory.GetDocumentBuffer(vsBuf) ?? _adapterFactory.GetDataBuffer(vsBuf);
                    if (buffer == null) return;
                    var text = buffer.CurrentSnapshot.GetText();
                    var fresh = MetadataParser.Parse(text);
                    var newText = AutoTagger.BuildInjectedText(text, tags, fresh);
                    if (newText == null) return;
                    using (var edit = buffer.CreateEdit())
                    {
                        var insertion = newText.Substring(fresh.CommentBlockEndExclusive,
                            newText.Length - text.Length);
                        edit.Insert(fresh.CommentBlockEndExclusive, insertion);
                        edit.Apply();
                    }
                    _log.Info($"[auto-tag injected] {string.Join(",", tags)} into {SafeName(info.Moniker)}");
                    ok = true;
                });
                return ok;
            }
            catch (Exception ex) { _log.Error("auto-tag injection failed", ex); return false; }
        }

        // ---------- polling ----------

        private void PollOnce()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    PollOnUiThread();
                });
            }
            catch (Exception ex) { _log.Error("poll tick failed", ex); }
        }

        private void PollOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<KeyValuePair<uint, SnapshotPipeline>> snapshot;
            lock (_pipelines) snapshot = new List<KeyValuePair<uint, SnapshotPipeline>>(_pipelines);

            foreach (var kv in snapshot)
            {
                try
                {
                    var text = ReadDocText(kv.Key);
                    if (text == null) continue;
                    kv.Value.OfferSnapshot(text, "edit");
                }
                catch (Exception ex) { _log.Error($"poll for cookie {kv.Key} failed", ex); }
            }
        }

        // ---------- helpers ----------

        private uint GetActiveDocCookie()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var monSel = (IVsMonitorSelection)_sp.GetService(typeof(SVsShellMonitorSelection));
            if (monSel == null) return 0;
            if (monSel.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object frameObj) != 0) return 0;
            if (!(frameObj is IVsWindowFrame frame)) return 0;
            if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocCookie, out object cookieObj) != 0) return 0;
            try { return Convert.ToUInt32(cookieObj); } catch { return 0; }
        }

        /// <summary>
        /// Returns the tab_id of the currently-active SSMS document, or null if no tracked
        /// SQL document is active. Must be called on the UI thread.
        /// </summary>
        public string GetActiveTabId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cookie = GetActiveDocCookie();
            if (cookie == 0) return null;
            SnapshotPipeline pipeline;
            lock (_pipelines) _pipelines.TryGetValue(cookie, out pipeline);
            return pipeline?.TabId;
        }

        private static string SafeName(string moniker)
        {
            try { return Path.GetFileName(moniker) ?? moniker; }
            catch { return moniker; }
        }
    }
}
