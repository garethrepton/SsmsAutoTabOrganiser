using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
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
        private readonly Func<bool> _isShuttingDown;
        private readonly Dictionary<uint, SnapshotPipeline> _pipelines = new Dictionary<uint, SnapshotPipeline>();
        private uint _cookie;
        private Timer _pollTimer;

        private DocumentTracker(IServiceProvider sp, IVsEditorAdaptersFactoryService adapterFactory,
                                SnapshotStore store, SettingsStore settings, Logger log,
                                Action<string> onTabUpdated, Action<string> onTabClosed,
                                Func<bool> isShuttingDown)
        {
            _sp = sp;
            _adapterFactory = adapterFactory;
            _rdt = new RunningDocumentTable(sp);
            _store = store;
            _settings = settings;
            _log = log;
            _onTabUpdated = onTabUpdated;
            _onTabClosed = onTabClosed;
            _isShuttingDown = isShuttingDown;
        }

        public static async Task<DocumentTracker> CreateAsync(IAsyncServiceProvider asp,
                                                              IVsEditorAdaptersFactoryService adapterFactory,
                                                              SnapshotStore store,
                                                              SettingsStore settings,
                                                              Logger log,
                                                              Action<string> onTabUpdated,
                                                              Action<string> onTabClosed,
                                                              Func<bool> isShuttingDown,
                                                              CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var t = new DocumentTracker((IServiceProvider)asp, adapterFactory, store, settings, log, onTabUpdated, onTabClosed, isShuttingDown);
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

            // Pre-check outside the lock so the SnapshotPipeline constructor (which touches
            // DTE/the store) doesn't run unnecessarily on the hot path.
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
                tryInjectId: (id, mk, meta, server) => InjectIdAtBufferOnUiThread(docCookie, id, meta, server),
                tryInjectAutoTags: tags => InjectAutoTagsAtBufferOnUiThread(docCookie, tags),
                isTabIdClaimedElsewhere: IsTabIdClaimedByOtherDoc,
                tryReplaceId: (oldId, newId) => ReplaceIdAtBufferOnUiThread(docCookie, oldId, newId));

            // Re-check under the lock: between the pre-check and now, a concurrent caller may
            // have inserted a pipeline for the same cookie. If so, dispose the one we just built
            // and use the existing one to avoid leaking a SnapshotPipeline (it owns no unmanaged
            // resources today, but it does hook into the store/settings, and pipelining a doc
            // twice would double the snapshot writes).
            bool inserted;
            lock (_pipelines)
            {
                if (_pipelines.ContainsKey(docCookie))
                {
                    inserted = false;
                }
                else
                {
                    _pipelines[docCookie] = pipeline;
                    inserted = true;
                }
            }
            if (!inserted) { try { pipeline.Dispose(); } catch { } return; }

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
                // SSMS shutdown path: skip the close-snapshot cascade. With many tabs open it
                // would write one redundant "closed" snapshot per tab through a single lock,
                // each followed by a UI refresh — adding seconds to shutdown for no value
                // (latest content is already snapshotted, and is_open is reset on next launch
                // by SnapshotStore.ClearAllOpenFlags).
                if (_isShuttingDown != null && _isShuttingDown()) return;

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

        private bool InjectIdAtBufferOnUiThread(uint docCookie, string id, ParsedMetadata meta, string server)
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
                    if (!string.IsNullOrEmpty(fresh.Id)) return;

                    // Untitled tabs with no comment header get a generated, searchable name
                    // line above the @id. Documents backed by a real file keep their first
                    // line untouched — renaming a saved script's panel entry to "New Tab …"
                    // would be worse than no header.
                    string header = null;
                    if (IsUntitledMoniker(info.Moniker))
                    {
                        header = "New Tab " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        if (!string.IsNullOrEmpty(server)) header += " on " + server;
                    }

                    var injection = MetadataWriter.ComputeIdInjection(text, id, header);
                    if (injection == null) return;
                    if (injection.InsertOffset < 0 || injection.InsertOffset > text.Length) return;
                    ApplyInsertPreservingCaret(vsBuf, buffer, injection.InsertOffset, injection.InsertedText);
                    _log.Info($"[@id injected] {id} into {SafeName(info.Moniker)} header={(header != null)}");
                    ok = true;
                });
                return ok;
            }
            catch (Exception ex) { _log.Error("@id injection failed", ex); return false; }
        }

        /// <summary>
        /// True when any other tracked open document has resolved the supplied tab id. Each
        /// pipeline asks this before honouring a file-borne @id, so a whole-query copy/paste
        /// (the @id line rides along with the copied text) can't interleave its snapshots
        /// with the original tab's history.
        /// </summary>
        private bool IsTabIdClaimedByOtherDoc(string tabId, string moniker)
        {
            if (string.IsNullOrEmpty(tabId)) return false;
            lock (_pipelines)
            {
                foreach (var p in _pipelines.Values)
                {
                    if (!string.Equals(p.TabId, tabId, StringComparison.Ordinal)) continue;
                    if (string.Equals(p.Moniker, moniker, StringComparison.OrdinalIgnoreCase)) continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Rewrites every <c>-- @id: oldId</c> line in the buffer to carry <paramref name="newId"/>,
        /// caret preserved. Returns false (so the pipeline retries on a later snapshot) when the
        /// buffer isn't adaptable or no line carries <paramref name="oldId"/> any more.
        /// </summary>
        private bool ReplaceIdAtBufferOnUiThread(uint docCookie, string oldId, string newId)
        {
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
                    var replacements = MetadataWriter.ComputeIdLineReplacements(text, oldId, newId);
                    if (replacements.Count == 0) return;
                    ApplyEditsPreservingCaret(vsBuf, buffer, edit =>
                    {
                        foreach (var r in replacements) edit.Replace(r.Start, r.Length, r.NewText);
                    });
                    _log.Info($"[@id reassigned] {oldId} -> {newId} in {SafeName(info.Moniker)}");
                    ok = true;
                });
                return ok;
            }
            catch (Exception ex) { _log.Error("@id reassign failed", ex); return false; }
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
                    // ComputeInjection returns the exact (offset, text) pair we need for
                    // ITextEdit.Insert. Previously we asked BuildInjectedText for the full new
                    // document and substring'd out the diff — a fragile pattern that would
                    // silently misbehave if BuildInjectedText were later changed to rewrite
                    // bytes outside the inserted segment.
                    var injection = AutoTagger.ComputeInjection(text, tags, fresh);
                    if (injection == null) return;
                    if (injection.InsertOffset < 0 || injection.InsertOffset > text.Length) return;
                    ApplyInsertPreservingCaret(vsBuf, buffer, injection.InsertOffset, injection.InsertedText);
                    _log.Info($"[auto-tag injected] {string.Join(",", tags)} into {SafeName(info.Moniker)}");
                    ok = true;
                });
                return ok;
            }
            catch (Exception ex) { _log.Error("auto-tag injection failed", ex); return false; }
        }

        /// <summary>
        /// Applies a single-point insertion to the buffer without moving any view's caret.
        /// The editor's default caret tracking is positive — an insertion exactly at the
        /// caret pushes it past the inserted text — so injecting the trailing @id while the
        /// user types at the end of a fresh tab would otherwise drag their cursor 40 blank
        /// lines down to sit after the @id line. A negative tracking point pins each caret
        /// to its pre-edit position instead.
        /// </summary>
        private void ApplyInsertPreservingCaret(IVsTextBuffer vsBuf, ITextBuffer buffer, int insertOffset, string insertedText)
            => ApplyEditsPreservingCaret(vsBuf, buffer, edit => edit.Insert(insertOffset, insertedText));

        private void ApplyEditsPreservingCaret(IVsTextBuffer vsBuf, ITextBuffer buffer, Action<ITextEdit> stage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var saved = new List<KeyValuePair<ITextView, ITrackingPoint>>();
            foreach (var view in GetTextViewsForBuffer(vsBuf))
            {
                try
                {
                    var caret = view.Caret.Position.BufferPosition;
                    saved.Add(new KeyValuePair<ITextView, ITrackingPoint>(
                        view, caret.Snapshot.CreateTrackingPoint(caret.Position, PointTrackingMode.Negative)));
                }
                catch { /* view mid-teardown — caret restore is best-effort */ }
            }

            using (var edit = buffer.CreateEdit())
            {
                stage(edit);
                edit.Apply();
            }

            foreach (var kv in saved)
            {
                try
                {
                    var view = kv.Key;
                    if (view.IsClosed) continue;
                    var restored = kv.Value.GetPoint(view.TextSnapshot);
                    if (view.Caret.Position.BufferPosition != restored)
                    {
                        view.Caret.MoveTo(restored);
                        view.Caret.EnsureVisible();
                    }
                }
                catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// True when the moniker belongs to a tab the user never saved: unrooted names, or
        /// files under %TEMP% — SSMS 22 backs every brand-new query window with a random
        /// file there (e.g. <c>Temp\mrdq5fad..sql</c>), so path-rootedness alone can't
        /// distinguish "untitled" from a real saved document.
        /// </summary>
        private static bool IsUntitledMoniker(string moniker)
        {
            try
            {
                if (string.IsNullOrEmpty(moniker) || !Path.IsPathRooted(moniker)) return true;
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + '\\';
                return Path.GetFullPath(moniker).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unparseable path: err on the side of NOT renaming — skip the header.
                return false;
            }
        }

        private List<ITextView> GetTextViewsForBuffer(IVsTextBuffer vsBuf)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var views = new List<ITextView>();
            try
            {
                var textManager = (IVsTextManager)_sp.GetService(typeof(SVsTextManager));
                if (textManager == null || vsBuf == null) return views;
                if (textManager.EnumViews(vsBuf, out IVsEnumTextViews enumViews) != VSConstants.S_OK || enumViews == null)
                    return views;

                var batch = new IVsTextView[1];
                uint fetched = 0;
                while (enumViews.Next(1, batch, ref fetched) == VSConstants.S_OK && fetched == 1 && batch[0] != null)
                {
                    try
                    {
                        var wpfView = _adapterFactory.GetWpfTextView(batch[0]);
                        if (wpfView != null && !wpfView.IsClosed) views.Add(wpfView);
                    }
                    catch { /* non-WPF view — nothing to preserve */ }
                    batch[0] = null;
                }
            }
            catch (Exception ex) { _log.Debug("EnumViews failed: " + ex.Message); }
            return views;
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

            // Pick up any open SQL docs we haven't attached yet. The startup RDT walk plus
            // OnAfterFirstDocumentLock should cover most cases, but tabs that were already
            // open before the package loaded — or ones whose buffer wasn't ready when the
            // event fired — can slip through. AttachIfSqlDoc is idempotent (dedupes on
            // _pipelines), so re-walking each tick is cheap.
            try
            {
                foreach (var info in _rdt) AttachIfSqlDoc(info.DocCookie, info.Moniker);
            }
            catch (Exception ex) { _log.Debug("RDT rescan failed: " + ex.Message); }

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

        /// <summary>
        /// When <paramref name="tabId"/> is already open as a tracked document, brings its
        /// window frame to the front and returns true — so "open" actions switch to the
        /// existing tab instead of materialising a duplicate copy. Frames are matched on RDT
        /// doc cookie rather than file path because two open tabs can legitimately share an
        /// on-disk path; the pipeline's resolved TabId is the identity that matters.
        /// Must be called on the UI thread.
        /// </summary>
        public bool TryActivateTab(string tabId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(tabId)) return false;

            uint cookie = 0;
            lock (_pipelines)
            {
                foreach (var kv in _pipelines)
                {
                    if (string.Equals(kv.Value.TabId, tabId, StringComparison.Ordinal)) { cookie = kv.Key; break; }
                }
            }
            if (cookie == 0) return false;

            try
            {
                var uiShell = (IVsUIShell)_sp.GetService(typeof(SVsUIShell));
                if (uiShell == null) return false;
                if (uiShell.GetDocumentWindowEnum(out IEnumWindowFrames framesEnum) != VSConstants.S_OK || framesEnum == null)
                    return false;

                var batch = new IVsWindowFrame[1];
                while (framesEnum.Next(1, batch, out uint fetched) == VSConstants.S_OK && fetched == 1 && batch[0] != null)
                {
                    var frame = batch[0];
                    batch[0] = null;
                    if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocCookie, out object cookieObj) != VSConstants.S_OK)
                        continue;
                    uint frameCookie;
                    try { frameCookie = Convert.ToUInt32(cookieObj); } catch { continue; }
                    if (frameCookie != cookie) continue;
                    return frame.Show() == VSConstants.S_OK;
                }
            }
            catch (Exception ex) { _log.Debug($"TryActivateTab({tabId}) failed: {ex.Message}"); }
            return false;
        }

        private static string SafeName(string moniker)
        {
            try { return Path.GetFileName(moniker) ?? moniker; }
            catch { return moniker; }
        }
    }
}
