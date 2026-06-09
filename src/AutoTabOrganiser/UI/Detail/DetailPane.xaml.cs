using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI.Detail
{
    /// <summary>
    /// Two-mode detail pane.
    ///
    /// READ mode (default): displays the selected <see cref="TabSummary"/>'s @name, @folder,
    /// @tags, connection, last-snapshot timestamp and the @desc rendered as markdown — the
    /// existing read-only layout. A small "Edit" button at the top-right toggles into edit mode.
    ///
    /// EDIT mode: TextBoxes for name / folder / tags / description over the same fixed pane
    /// height. Save patches the live SSMS editor buffer for the selected tab via SSMS's DTE
    /// TextDocument; the next snapshot tick (DocumentTracker polls at 500ms) picks the edit up
    /// and persists it through the snapshot pipeline, which in turn refreshes the side panel.
    /// Cancel reverts and returns to read mode.
    ///
    /// Editing closed tabs is intentionally not yet supported (see <see cref="OnEditClicked"/>):
    /// patching the latest snapshot's .sql on disk would require either a new package-level
    /// API or duplicating the on-disk read/write path from <c>SnapshotStore</c>. Reopening the
    /// tab unblocks the editor — that path is documented in the disabled-button tooltip.
    /// </summary>
    internal partial class DetailPane : UserControl
    {
        // Currently-displayed tab. Null when Show(null)/Clear()/ShowError() has been called.
        // Captured so OnSaveClicked knows which tab id to patch back into SSMS.
        private TabSummary _current;

        // History-timeline dependencies, injected by the parent control after construction.
        // Null until Configure runs; the HISTORY section stays collapsed in that state.
        private SnapshotStore _store;
        private Func<SnapshotRecord, Task> _restoreAsNewTab;

        public DetailPane() { InitializeComponent(); }

        /// <summary>
        /// Wire the snapshot store (history list + diff content reads) and the restore
        /// callback ("Open copy" → package opens the old version as a brand-new tab).
        /// </summary>
        public void Configure(SnapshotStore store, Func<SnapshotRecord, Task> restoreAsNewTab)
        {
            _store = store;
            _restoreAsNewTab = restoreAsNewTab;
        }

        // ---------- public surface (unchanged signatures — the parent depends on these) ----------

        public void Show(TabSummary t)
        {
            // Defensive: any time the selection changes, drop out of edit mode. Otherwise the
            // user might think their pending edits applied to the new selection.
            ExitEditMode();

            _current = t;

            ErrorText.Text = "";
            TitleText.Text = string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name;
            BreadcrumbText.Text = string.IsNullOrEmpty(t.Folder) ? "Unfiled" : t.Folder;

            var tags = string.IsNullOrEmpty(t.TagsCsv)
                ? Array.Empty<string>()
                : t.TagsCsv.Split(',').Select(s => "#" + s).ToArray();
            TagChips.ItemsSource = tags;

            ConnectionText.Text = FormatConnection(t.Server, t.Database);
            LastSnapshotText.Text = "Last snapshot: " +
                DateTimeOffset.FromUnixTimeMilliseconds(t.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

            if (string.IsNullOrEmpty(t.Desc))
            {
                MarkdownHost.Document = new FlowDocument();
            }
            else
            {
                try
                {
                    MarkdownHost.Document = MarkdownFlowDocumentRenderer.Render(t.Desc);
                }
                catch (Exception ex)
                {
                    MarkdownHost.Document = new FlowDocument();
                    ErrorText.Text = "Markdown render failed: " + ex.Message;
                }
            }

            // Edit button: only enabled if the tab is currently open in SSMS. Closed-tab editing
            // is out of scope for this round (see class-level XML doc); we still surface the
            // button so the affordance is discoverable, but the tooltip explains the gate.
            UpdateEditButtonEnabled();
            RefreshHistory(t);
        }

        public void Clear()
        {
            ExitEditMode();
            _current = null;

            TitleText.Text = "";
            BreadcrumbText.Text = "";
            TagChips.ItemsSource = null;
            ConnectionText.Text = "";
            LastSnapshotText.Text = "";
            MarkdownHost.Document = new FlowDocument();
            ErrorText.Text = "";
            UpdateEditButtonEnabled();
            HistoryList.ItemsSource = null;
            HistoryExpander.Visibility = Visibility.Collapsed;
        }

        public void ShowError(string message)
        {
            Clear();
            ErrorText.Text = message;
        }

        // ---------- snapshot history timeline ----------

        private sealed class HistoryRow
        {
            public SnapshotRecord Record;
            public string Display { get; set; }
            public override string ToString() => Display;
        }

        /// <summary>
        /// Populate the HISTORY list with every snapshot of the selected tab, newest first.
        /// Metadata-only query (no content) served by the (tab_id, ts DESC) index, so a
        /// synchronous load here matches the cost profile of the rest of the pane.
        /// </summary>
        private void RefreshHistory(TabSummary t)
        {
            HistoryList.ItemsSource = null;
            HistoryHeaderText.Text = "HISTORY";
            if (_store == null || t == null || string.IsNullOrEmpty(t.TabId))
            {
                HistoryExpander.Visibility = Visibility.Collapsed;
                return;
            }

            List<SnapshotRecord> snaps;
            try
            {
                snaps = _store.ListSnapshots("tab_id=$t",
                    new[] { new KeyValuePair<string, object>("$t", t.TabId) }, 200);
            }
            catch (Exception ex)
            {
                HistoryExpander.Visibility = Visibility.Collapsed;
                ErrorText.Text = "History load failed: " + ex.Message;
                return;
            }

            var rows = new List<HistoryRow>(snaps.Count);
            for (int i = 0; i < snaps.Count; i++)
            {
                var r = snaps[i];
                var older = i + 1 < snaps.Count ? snaps[i + 1] : null;
                var when = DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                var delta = older == null ? "" : FormatSizeDelta(r.ContentSize - older.ContentSize);
                rows.Add(new HistoryRow
                {
                    Record = r,
                    Display = $"{when}  {r.Reason,-6}  {FormatSize(r.ContentSize)}{delta}"
                });
            }
            HistoryList.ItemsSource = rows;
            HistoryHeaderText.Text = $"HISTORY ({rows.Count})";
            HistoryExpander.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }

        private static string FormatSizeDelta(long delta)
        {
            if (delta == 0) return "";
            var sign = delta > 0 ? "+" : "−";
            return $"  ({sign}{FormatSize(Math.Abs(delta))})";
        }

        private static string SnapshotLabel(SnapshotRecord r)
            => DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
               + " (" + r.Reason + ")";

        private void OnHistoryDiffClicked(object sender, RoutedEventArgs e)
        {
            if (_store == null) return;
            var all = HistoryList.ItemsSource as List<HistoryRow>;
            if (all == null || all.Count == 0) return;
            var selected = HistoryList.SelectedItems.OfType<HistoryRow>()
                .OrderByDescending(r => r.Record.Ts).ToList();

            HistoryRow newer, older;
            if (selected.Count >= 2)
            {
                newer = selected[0];
                older = selected[selected.Count - 1];
            }
            else if (selected.Count == 1)
            {
                newer = all[0]; // latest
                older = selected[0];
                if (ReferenceEquals(newer, older))
                {
                    if (all.Count < 2) { ErrorText.Text = "Only one snapshot exists — nothing to diff."; return; }
                    older = all[1]; // latest vs previous
                }
            }
            else
            {
                ErrorText.Text = "Select a snapshot (or Ctrl+Click two) to diff.";
                return;
            }

            ErrorText.Text = "";
            var store = _store;
            var oldRec = older.Record;
            var newRec = newer.Record;
            var title = "snapshot diff — " + (_current?.Name ?? "(unnamed)");

            // Content reads + LCS off the UI thread; only the dialog opens back on it.
            _ = Task.Run(() =>
            {
                string diff;
                try
                {
                    var oldText = store.ReadSnapshotContentById(oldRec.Id) ?? "";
                    var newText = store.ReadSnapshotContentById(newRec.Id) ?? "";
                    diff = TextDiff.Unified(oldText, newText, SnapshotLabel(oldRec), SnapshotLabel(newRec));
                    if (string.IsNullOrEmpty(diff)) diff = "(no changes between these snapshots)";
                }
                catch (Exception ex) { diff = "Error: " + ex.Message; }
                Dispatcher.BeginInvoke((Action)(() => Diff.DiffDialog.Show(title, diff)));
            });
        }

        private void OnHistoryRestoreClicked(object sender, RoutedEventArgs e)
        {
            var row = HistoryList.SelectedItem as HistoryRow;
            if (row == null) { ErrorText.Text = "Select a snapshot to open."; return; }
            ErrorText.Text = "";
            _ = _restoreAsNewTab?.Invoke(row.Record);
        }

        // ---------- read-mode helpers ----------

        private static string FormatConnection(string server, string database)
        {
            var s = (server ?? "").Trim();
            var d = (database ?? "").Trim();
            if (s.Length == 0 && d.Length == 0) return "";
            if (s.Length == 0) return "Connection: · " + d;
            if (d.Length == 0) return "Connection: " + s;
            return "Connection: " + s + " · " + d;
        }

        private void UpdateEditButtonEnabled()
        {
            bool canEdit = _current != null && _current.IsOpen;
            EditButton.IsEnabled = canEdit;
            EditButton.ToolTip = canEdit
                ? "Edit metadata (name, folder, tags, description)"
                // TODO: support editing closed tabs by patching the latest snapshot's .sql on
                // disk via MetadataWriter, then re-importing through SnapshotStore.WriteSnapshot.
                // Currently out of scope — reopen the tab to edit.
                : "Reopen the tab to edit its metadata (closed-tab editing not yet supported).";
        }

        // ---------- edit-mode toggle ----------

        private void OnEditClicked(object sender, RoutedEventArgs e)
        {
            if (_current == null || !_current.IsOpen) return;

            EditNameBox.Text   = _current.Name   ?? string.Empty;
            EditFolderBox.Text = _current.Folder ?? string.Empty;
            EditTagsBox.Text   = (_current.TagsCsv ?? string.Empty);
            EditDescBox.Text   = _current.Desc   ?? string.Empty;
            EditErrorText.Text = string.Empty;

            ReadModeHost.Visibility = Visibility.Collapsed;
            EditModeHost.Visibility = Visibility.Visible;

            // Land focus on the name box and select all so the user can immediately start typing
            // to replace, matching the muscle-memory pattern of the save-to-scripts prompt.
            EditNameBox.Focus();
            EditNameBox.SelectAll();
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e) => ExitEditMode();

        private void ExitEditMode()
        {
            EditModeHost.Visibility = Visibility.Collapsed;
            ReadModeHost.Visibility = Visibility.Visible;
            EditErrorText.Text = string.Empty;
        }

        // ---------- save: patch the live SSMS buffer ----------

        private void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            // WPF Click handler — already on the VS UI thread. Assert for the analyser.
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var tabId = _current.TabId;
            if (string.IsNullOrEmpty(tabId)) { ShowEditError("This row has no tab id."); return; }

            // Snapshot the inputs first — control text properties can race with focus changes.
            var newName   = (EditNameBox.Text   ?? string.Empty).Trim();
            var newFolder = (EditFolderBox.Text ?? string.Empty).Trim().Trim('/');
            var newTagsCsv = NormaliseTagsCsv(EditTagsBox.Text);
            var newDesc   = EditDescBox.Text ?? string.Empty;

            try
            {
                if (!TryApplyToOpenTab(tabId, newName, newFolder, newTagsCsv, newDesc, out var error))
                {
                    ShowEditError(error ?? "Could not locate the open SSMS tab for this row. Bring it to the foreground and retry.");
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowEditError("Save failed: " + ex.Message);
                return;
            }

            // Buffer patched. DocumentTracker.PollOnce (500ms cadence) will offer the new text to
            // SnapshotPipeline, which writes a snapshot and fires onTabUpdated → tool-window
            // refresh. We optimistically update the read-mode display now so the user gets
            // immediate visual feedback rather than waiting up to a second.
            _current.Name    = string.IsNullOrEmpty(newName)   ? null : newName;
            _current.Folder  = string.IsNullOrEmpty(newFolder) ? null : newFolder;
            _current.TagsCsv = string.IsNullOrEmpty(newTagsCsv) ? null : newTagsCsv;
            _current.Desc    = string.IsNullOrEmpty(newDesc)   ? null : newDesc;
            Show(_current);
        }

        private void ShowEditError(string message)
        {
            EditErrorText.Text = message ?? string.Empty;
        }

        /// <summary>
        /// Tags input is a CSV without leading '#'. Tolerate the user pasting "#foo, #bar" or
        /// "foo bar" — normalise to a comma-separated lowercase form that matches the storage
        /// convention (snapshot_tags / tabs_latest.tags_csv).
        /// </summary>
        private static string NormaliseTagsCsv(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var parts = raw.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                var t = p.Trim().TrimStart('#').ToLowerInvariant();
                if (t.Length == 0) continue;
                if (!seen.Add(t)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(t);
            }
            return sb.ToString();
        }

        // ---------- live-buffer integration via DTE ----------

        /// <summary>
        /// Locates the SSMS document whose @id matches <paramref name="tabId"/>, replaces its
        /// entire text with a re-rendered header carrying the new metadata values, and lets
        /// DocumentTracker's poll cycle pick up the change for snapshotting.
        /// </summary>
        private bool TryApplyToOpenTab(string tabId, string name, string folder, string tagsCsv, string desc, out string error)
        {
            error = null;

            // Button click is dispatched on the WPF UI thread, which is the same thread VS
            // marshals its main-thread-affine COM types onto. Assert it explicitly so the
            // VSTHRD010 analyser is satisfied and the call below is documentation-correct.
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE.DTE dte = null;
            try { dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE; }
            catch { /* fall through to null check */ }
            if (dte == null) { error = "SSMS DTE service unavailable."; return false; }

            EnvDTE.Document targetDoc = null;
            EnvDTE.TextDocument targetText = null;
            string targetText0 = null;

            try
            {
                foreach (EnvDTE.Document d in dte.Documents)
                {
                    EnvDTE.TextDocument td = null;
                    try { td = d?.Object("TextDocument") as EnvDTE.TextDocument; }
                    catch { td = null; }
                    if (td == null) continue;

                    string body = null;
                    try
                    {
                        // EditPoint.GetText is the canonical "read the whole buffer" path in DTE.
                        var sp = td.StartPoint.CreateEditPoint();
                        body = sp.GetText(td.EndPoint);
                    }
                    catch { body = null; }
                    if (string.IsNullOrEmpty(body)) continue;

                    // Match by parsed @id. This is the only identity stable across save/rename —
                    // file paths in the open/ staging folder shift if the user renames the tab,
                    // and saved-script paths can be missing entirely for never-saved tabs.
                    var parsed = MetadataParser.Parse(body);
                    if (!string.Equals(parsed?.Id, tabId, StringComparison.OrdinalIgnoreCase)) continue;

                    targetDoc = d;
                    targetText = td;
                    targetText0 = body;
                    break;
                }
            }
            catch (Exception ex)
            {
                error = "Could not enumerate SSMS documents: " + ex.Message;
                return false;
            }

            if (targetText == null || targetText0 == null)
            {
                error = "No open SSMS document matched this tab id. Reopen the tab and retry.";
                return false;
            }

            // Compose the new text. We keep MetadataWriter as the source of truth for the keys
            // it already owns (folder), and apply name/tags/description through a leading-key
            // upsert that mirrors MetadataWriter.SetLeadingKey. Description uses the multi-line
            // `@desc: |` block form that MetadataParser supports.
            var updated = MetadataWriter.SetFolder(targetText0, folder ?? string.Empty);
            updated = UpsertLeadingKey(updated, "name", name ?? string.Empty);
            updated = ReplaceTagLine(updated, tagsCsv ?? string.Empty);
            updated = UpsertDescriptionBlock(updated, desc ?? string.Empty);

            if (string.Equals(updated, targetText0, StringComparison.Ordinal)) return true; // no-op

            try
            {
                // Whole-buffer replace via an EditPoint. DTE batches this as a single Undo unit
                // so the user can Ctrl+Z to revert if they didn't mean to save.
                var start = targetText.StartPoint.CreateEditPoint();
                start.ReplaceText(targetText.EndPoint, updated,
                    (int)EnvDTE.vsEPReplaceTextOptions.vsEPReplaceTextAutoformat);
            }
            catch (Exception ex)
            {
                error = "Edit failed: " + ex.Message;
                return false;
            }

            return true;
        }

        // ---------- header rewriters ----------
        //
        // These mirror the shape of MetadataWriter.SetLeadingKey (which is private). They MUST
        // stay byte-compatible with what MetadataParser expects — namely a leading block of
        // `-- @key: value` lines, optionally with a `-- @key: |` multi-line continuation for
        // description. If MetadataWriter ever exposes a public `SetLeadingKey(text, key, value)`
        // or a `SetName`/`SetTags`/`SetDescription` trio, we should delete this and call into
        // it directly. Until then this small duplication keeps the data invariant correct
        // without touching the writer file.

        /// <summary>
        /// Inserts or replaces a leading <c>-- @key: value</c> line. Empty <paramref name="value"/>
        /// removes the existing line outright (matches the parser's "missing key" semantics so
        /// the side-panel display stays consistent).
        /// </summary>
        private static string UpsertLeadingKey(string text, string key, string value)
        {
            text = text ?? string.Empty;
            value = value ?? string.Empty;
            var nl = DetectNewline(text);

            int i = 0, len = text.Length;
            int keyLineStart = -1, keyLineEndExclusive = -1;
            int firstCommentLineStart = -1;

            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int lineEndContent = i;
                if (i < len) i++;

                var raw = text.Substring(lineStart, lineEndContent - lineStart);
                if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
                var trimmed = raw.TrimStart();

                if (trimmed.Length == 0) break;
                if (!trimmed.StartsWith("--")) break;

                if (firstCommentLineStart < 0) firstCommentLineStart = lineStart;

                if (keyLineStart < 0)
                {
                    var rest = trimmed.Substring(2).TrimStart();
                    var token = "@" + key;
                    if (rest.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                        && (rest.Length == token.Length || rest[token.Length] == ':' || char.IsWhiteSpace(rest[token.Length])))
                    {
                        keyLineStart = lineStart;
                        keyLineEndExclusive = i;
                    }
                }
            }

            if (value.Length == 0)
            {
                // Remove the line if present; otherwise no-op. We don't synthesise an empty line.
                if (keyLineStart >= 0)
                    return text.Substring(0, keyLineStart) + text.Substring(keyLineEndExclusive);
                return text;
            }

            var newLine = "-- @" + key + ": " + value + nl;

            if (keyLineStart >= 0)
                return text.Substring(0, keyLineStart) + newLine + text.Substring(keyLineEndExclusive);
            if (firstCommentLineStart >= 0)
                return text.Substring(0, firstCommentLineStart) + newLine + text.Substring(firstCommentLineStart);
            return newLine + nl + text;
        }

        /// <summary>
        /// Replaces the description block. Multi-line markdown is preferable so authors aren't
        /// forced onto a single line. Uses the parser's <c>@desc: |</c> continuation form: each
        /// non-empty line of <paramref name="desc"/> becomes <c>-- &lt;line&gt;</c> until a blank
        /// (non-comment) line terminates the leading block.
        ///
        /// If <paramref name="desc"/> is empty, any existing @desc/@description block is removed.
        /// </summary>
        private static string UpsertDescriptionBlock(string text, string desc)
        {
            text = text ?? string.Empty;
            desc = (desc ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            var nl = DetectNewline(text);

            // Locate the existing @desc or @description range first. Both single-line and
            // multi-line forms are handled.
            int len = text.Length;
            int i = 0;
            int descStart = -1, descEndExclusive = -1;
            int firstCommentLineStart = -1;

            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int lineEndContent = i;
                if (i < len) i++;

                var raw = text.Substring(lineStart, lineEndContent - lineStart);
                if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
                var trimmed = raw.TrimStart();

                if (trimmed.Length == 0) break;
                if (!trimmed.StartsWith("--")) break;

                if (firstCommentLineStart < 0) firstCommentLineStart = lineStart;

                if (descStart < 0)
                {
                    var rest = trimmed.Substring(2).TrimStart();
                    bool isDesc =
                        StartsWithKey(rest, "desc") ||
                        StartsWithKey(rest, "description");
                    if (isDesc)
                    {
                        descStart = lineStart;
                        descEndExclusive = i;
                        // If it's the multi-line form (`|`), absorb continuation comment lines
                        // until we hit another @key line or a blank/non-comment line.
                        var afterColon = AfterColon(rest);
                        if (afterColon == "|")
                        {
                            int j = i;
                            while (j < len)
                            {
                                int s = j;
                                while (j < len && text[j] != '\n') j++;
                                int ec = j;
                                if (j < len) j++;
                                var line = text.Substring(s, ec - s);
                                if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
                                var lt = line.TrimStart();
                                if (lt.Length == 0) break;
                                if (!lt.StartsWith("--")) break;
                                // Another @key line ends the multi-line block.
                                var after = lt.Substring(2).TrimStart();
                                if (after.StartsWith("@")) break;
                                descEndExclusive = j;
                            }
                        }
                    }
                }
            }

            // Build the replacement.
            string replacement;
            if (desc.Length == 0)
            {
                replacement = string.Empty;
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append("-- @desc: |").Append(nl);
                foreach (var line in desc.Split('\n'))
                {
                    // The parser's StripCommentLeader chews "-- " then up to two spaces. We emit
                    // "-- " + line so round-tripping (parse → reformat → re-parse) is stable.
                    sb.Append("-- ").Append(line).Append(nl);
                }
                replacement = sb.ToString();
            }

            if (descStart >= 0)
                return text.Substring(0, descStart) + replacement + text.Substring(descEndExclusive);

            if (replacement.Length == 0) return text;

            if (firstCommentLineStart >= 0)
                return text.Substring(0, firstCommentLineStart) + replacement + text.Substring(firstCommentLineStart);

            return replacement + nl + text;
        }

        /// <summary>
        /// Replaces (or removes) the leading <c>-- #tag1, #tag2</c> tag line. Tags are stored as
        /// bare <c>#word</c> tokens anywhere in the leading comment block; the parser scans for
        /// them with <see cref="MetadataParser.ExtractTagsFromHeaderComments"/>. We use a dedicated
        /// <c>-- @tags</c>-style line so future edits can find it deterministically.
        ///
        /// <paramref name="csv"/> may be empty — in which case the existing tag line is removed.
        /// </summary>
        private static string ReplaceTagLine(string text, string csv)
        {
            text = text ?? string.Empty;
            csv = (csv ?? string.Empty).Trim();
            var nl = DetectNewline(text);

            // Find any pre-existing "-- @tags" line in the leading comment block. We use a key
            // line (rather than a free-form "#tag1 #tag2" comment) so subsequent edits can locate
            // and replace it deterministically — the parser still picks up the #tags either way.
            int len = text.Length;
            int i = 0;
            int tagLineStart = -1, tagLineEnd = -1;
            int firstCommentLineStart = -1;

            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int lineEndContent = i;
                if (i < len) i++;

                var raw = text.Substring(lineStart, lineEndContent - lineStart);
                if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
                var trimmed = raw.TrimStart();

                if (trimmed.Length == 0) break;
                if (!trimmed.StartsWith("--")) break;

                if (firstCommentLineStart < 0) firstCommentLineStart = lineStart;

                if (tagLineStart < 0)
                {
                    var rest = trimmed.Substring(2).TrimStart();
                    if (StartsWithKey(rest, "tags"))
                    {
                        tagLineStart = lineStart;
                        tagLineEnd = i;
                    }
                }
            }

            string newLine = string.Empty;
            if (csv.Length > 0)
            {
                var hashed = string.Join(" ",
                    csv.Split(',').Select(t => "#" + t.Trim()).Where(t => t.Length > 1));
                newLine = "-- @tags: " + hashed + nl;
            }

            if (tagLineStart >= 0)
                return text.Substring(0, tagLineStart) + newLine + text.Substring(tagLineEnd);
            if (newLine.Length == 0) return text;
            if (firstCommentLineStart >= 0)
                return text.Substring(0, firstCommentLineStart) + newLine + text.Substring(firstCommentLineStart);
            return newLine + nl + text;
        }

        // ---- parsing helpers (kept local; mirror MetadataWriter/MetadataParser invariants) ----

        private static bool StartsWithKey(string rest, string key)
        {
            if (!rest.StartsWith("@" + key, StringComparison.OrdinalIgnoreCase)) return false;
            var n = key.Length + 1;
            if (rest.Length == n) return true;
            var c = rest[n];
            return c == ':' || char.IsWhiteSpace(c);
        }

        private static string AfterColon(string rest)
        {
            // rest is everything after `--` (already trimmed). Skip past "@key" then ":" then ws.
            int i = 0;
            while (i < rest.Length && !(rest[i] == ':' || char.IsWhiteSpace(rest[i]))) i++;
            while (i < rest.Length && (rest[i] == ':' || char.IsWhiteSpace(rest[i]))) i++;
            return i >= rest.Length ? string.Empty : rest.Substring(i).Trim();
        }

        private static string DetectNewline(string text)
        {
            if (text == null) return Environment.NewLine;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') return (i > 0 && text[i - 1] == '\r') ? "\r\n" : "\n";
            }
            return Environment.NewLine;
        }
    }
}
