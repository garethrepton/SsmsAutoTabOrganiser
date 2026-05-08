# Auto Tab Organiser & Organiser - Specification

## Overview

A Visual Studio extension (VSIX) targeting **SQL Server Management Studio 21+**
that provides automatic version history and self-organising workspace
management for SQL query tabs.

The extension auto-snapshots every open SQL tab on edit, save, and close. It
parses a comment block at the top of each tab for metadata (`@folder`, `@name`,
`@desc`) and uses that metadata to populate a sidebar tree, giving the user a
self-organising "solution-like" view of their work. Markdown descriptions are
rendered in a detail pane.

There is no explicit "save" UI - the comment block is the source of truth.

## Goals

- Zero-friction history: nothing to click, all tabs continuously snapshotted.
- Declarative organisation: folder structure derived from comment metadata.
- Portable metadata: `.sql` files remain meaningful outside the extension.
- Crash/close recovery: never lose a tab's contents to an SSMS crash or
  accidental close.
- Searchable: find any historical version of any query by content or metadata.

## Non-goals (v1)

- Auto-reconnect to the right server/database when opening a historical tab.
- Cross-machine sync (the snapshot store is local).
- Drag-and-drop reorganisation in the sidebar (folders come from comments,
  not the tree).
- Inline markdown rendering in the SSMS editor.
- Sharing/collaboration features.

## Target environment

- **SSMS 21+** (Visual Studio 2022 shell, 17.x).
- **.NET Framework 4.7.2** (required by the VS 2022 extension model).
- **Windows 10/11**.
- Developed in Visual Studio 2022 with the "Visual Studio extension
  development" workload.

The VSIX manifest must declare both VS 2022 and SSMS 21 as install targets.

## Architecture

Three logical components, plus a UI layer:

1. **Document Tracker** - subscribes to VS document/text-buffer events for all
   SQL editor windows. Fires snapshot events.
2. **Snapshot Store** - persists snapshots to disk (file per snapshot) with a
   SQLite index for fast querying.
3. **Metadata Engine** - parses the leading comment block, extracts folder
   path, name, description, id; injects an `@id` line on first snapshot;
   updates the live tree model.
4. **UI** - one tool window with tabs ("Tabs" and "History"), implementing the
   sidebar tree and markdown detail pane.

### Data flow

```
[SSMS editor] --text changes--> [Document Tracker]
                                       |
                                  debounce 5s
                                       |
                                       v
                             [Metadata Engine] ---parses comment block---
                                       |                                 |
                                       v                                 v
                              [Snapshot Store] <-----writes snapshot-----+
                                       |
                                       v
                                  [Tree Model] ---notifies---> [Sidebar UI]
```

## Comment metadata format

Parsed from the **first contiguous comment block** at the top of the file.
Stops at the first blank line or first non-comment line.

Single-line keys:

```sql
-- @folder: Investigations/PROD-1234
-- @name: Find slow queries
-- #prod #performance
-- @id: 7f3c-a1b2-9c8d
```

### Tags

Tags are `#word` tokens — `#` followed by `[A-Za-z0-9_-]+` (minimum 2
characters after the `#`) — and may appear **anywhere in the file**, not
only in the leading comment block. The indexer scans the entire document.

To avoid false positives in SQL bodies, only tokens that are inside a
T-SQL comment count as tags:

- Inside a `--` line comment, from `--` to end-of-line.
- Inside a `/* ... */` block comment, including multi-line blocks.

A `#` outside any comment (e.g. `#temp` table names, `[#col]` identifiers)
is **not** indexed as a tag. This rule is mechanical — the parser tracks
comment state across the document and only emits tags from inside comments.

Examples (all valid, all indexed):

```sql
-- #prod #performance
SELECT * FROM dbo.Orders;  -- #slow-query investigated 2026-04
/*
  See ticket #PROD-1234 — also touches #lock-escalation.
*/
SELECT * FROM #temp_results;  -- '#temp_results' is NOT a tag (outside comment)
```

Tag indexing rules:

- Tags are stored deduplicated per snapshot, lowercased, in the
  `snapshot_tags` table (see schema).
- Re-extracted on every snapshot, so editing or removing a `#tag` updates
  the index on the next snapshot.
- The leading `#` is stripped in storage (`prod`, `performance`,
  `prod-1234`).
- Markdown headings inside a `@desc` body are ambiguous — a line starting
  with `# Heading` is treated as a tag `heading` only if `Heading` matches
  the tag regex. Authors who want true markdown headings should use `##` or
  bold instead, or accept the indexed tag.

Multi-line value (description) using YAML-style `|` continuation. Every
subsequent line that begins with `-- ` (with the leading space) and is more
indented than the key, or has no `--` prefix's content, is part of the value.
The value stops at the first comment line that introduces a new `@key:` or at
the first non-comment line.

```sql
-- @folder: Investigations/PROD-1234
-- @name: Find slow queries
-- @desc: |
--   Looks for queries running > 5s in the last hour.
--   
--   **Watch out**: this hits `sys.dm_exec_query_stats` which is
--   server-wide.
--   
--   See [PROD-1234](https://jira.example/PROD-1234) for context.
-- @id: 7f3c-a1b2-9c8d
SELECT ...
```

### Recognised keys (v1)

| Key       | Type            | Behaviour                                         |
|-----------|-----------------|---------------------------------------------------|
| `@folder` | `path/with/slashes` | Determines sidebar location. Missing -> `Unfiled`. |
| `@name`   | string          | Display name. Missing -> first non-comment, non-blank line truncated to 60 chars; failing that, the SSMS tab title. |
| `@desc`   | markdown        | Rendered in detail pane. Multi-line via `|`.      |
| `@id`     | uuid-ish        | Stable identity. Auto-injected if missing.        |
| `@nosnapshot` | flag (no value) | Disables history for this tab.                |
| `#tag`    | hashtag tokens  | Any `#word` inside any SQL comment, anywhere in the file. Indexed, searchable, shown as chips in the detail pane. |

Unknown `@keys` are preserved but ignored. The parser must round-trip safely.

### `@id` injection

On a tab's first snapshot, if no `@id` is present:

1. Generate a short id (`{8 hex}-{4 hex}-{4 hex}`, ~16 chars).
2. Insert `-- @id: <value>` as the **last line of the existing comment block**,
   immediately before the first non-comment line.
3. If there is no comment block at all, do nothing - we don't inject into a
   tab that has no metadata yet (avoid surprising the user). The tab still
   gets snapshotted; identity falls back to content fingerprinting.
4. The injection is a normal text edit performed on the UI thread via
   `ITextBuffer.Replace`.
5. Never inject while the user's cursor is on lines 1-3 of the document.
   Defer until the cursor moves away.
6. Setting: `AutoInjectId` (default `true`). When `false`, never inject;
   rely entirely on content fingerprinting for identity.

## Snapshotting

### Triggers

| Trigger              | Reason code | Notes                                           |
|----------------------|-------------|-------------------------------------------------|
| Text buffer changed  | `edit`      | Debounced 5s (configurable 2-30s).              |
| Document saved       | `saved`     | Immediate. Always retained by pruning.          |
| Document closed      | `closed`    | Immediate. Always retained by pruning.          |
| Periodic flush       | `flush`     | Every 60s if dirty and debounce hasn't fired.   |

### Deduplication

Each snapshot computes SHA-256 of the full document text. If the hash matches
the previous snapshot for the same tab id, the snapshot is skipped - except
when `reason` is `saved` or `closed`, which are always retained.

### Snapshot record

Stored as a `.sql` file on disk, plus rows in the SQLite index across three
tables:

```sql
CREATE TABLE snapshots (
  id              TEXT PRIMARY KEY,    -- snapshot uuid
  tab_id          TEXT NOT NULL,       -- @id from metadata, or fingerprint
  file_path       TEXT,                -- if the SSMS tab had one, else NULL
  folder          TEXT,                -- parsed from @folder
  name            TEXT,                -- parsed from @name (or fallbacks)
  content_hash    TEXT NOT NULL,       -- sha256 hex
  content_size    INTEGER NOT NULL,    -- bytes
  disk_path       TEXT NOT NULL,       -- relative path within snapshots/
  reason          TEXT NOT NULL,       -- edit|saved|closed|flush
  ts              INTEGER NOT NULL,    -- unix epoch ms
  server          TEXT,                -- best-effort, may be NULL
  database        TEXT                 -- best-effort, may be NULL
);
CREATE INDEX ix_snapshots_tab_ts   ON snapshots(tab_id, ts DESC);
CREATE INDEX ix_snapshots_ts       ON snapshots(ts DESC);
CREATE INDEX ix_snapshots_folder   ON snapshots(folder);
CREATE INDEX ix_snapshots_name_nc  ON snapshots(name COLLATE NOCASE);

-- Tags extracted from this specific snapshot's content.
CREATE TABLE snapshot_tags (
  snapshot_id     TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  tag             TEXT NOT NULL,       -- lowercased, no leading '#'
  PRIMARY KEY (snapshot_id, tag)
);
CREATE INDEX ix_snapshot_tags_tag  ON snapshot_tags(tag);

-- Denormalised view of the *latest* snapshot per tab_id, used to drive the
-- side panel without aggregating on every render.
CREATE TABLE tabs_latest (
  tab_id          TEXT PRIMARY KEY,
  latest_snapshot_id TEXT NOT NULL REFERENCES snapshots(id),
  folder          TEXT,
  name            TEXT,
  tags_csv        TEXT,                -- denormalised, lowercased, sorted
  ts              INTEGER NOT NULL,    -- ts of latest snapshot
  is_open         INTEGER NOT NULL DEFAULT 0,
  is_dirty        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_tabs_latest_ts     ON tabs_latest(ts DESC);
CREATE INDEX ix_tabs_latest_name   ON tabs_latest(name COLLATE NOCASE);
```

`tabs_latest` is updated transactionally with each new snapshot insert. The
side panel reads from `tabs_latest` (joined to `snapshot_tags` via
`latest_snapshot_id` when filtering by tag); the History tab reads directly
from `snapshots`.

### Storage layout

The storage root defaults to `%LOCALAPPDATA%\AutoTabOrganiser\` but is
configurable via the `StorageLocation` setting (see Settings). The settings
file itself lives in a fixed location (see Settings) so the chosen storage
root survives across reinstalls and machines.

```
<StorageRoot>\
  index.db                              -- SQLite
  snapshots\
    YYYY\MM\DD\
      HH-MM-SS_<shortid>_<safe-title>.sql
  open\                                 -- temp files for opening history
  logs\
    YYYY-MM-DD.log
```

#### Snapshot filename

`safe-title` is derived from the **first non-blank, non-comment line** of the
document at snapshot time:

1. Strip leading/trailing whitespace.
2. Strip a trailing `;`.
3. Truncate to 60 characters.
4. Sanitise to `[A-Za-z0-9._ -]`, replacing other characters with `_`.
5. Collapse runs of `_` and trim them from both ends.
6. If the result is empty, fall back to the parsed `@name`, then to the SSMS
   tab title, then to `untitled`.

The first-line rule means a query starting with
`SELECT TOP 100 * FROM dbo.Customers` produces a file like
`14-23-05_a1b2c3d4_SELECT TOP 100 _ FROM dbo.Customers.sql`. This is purely
cosmetic — identity is still driven by `tab_id` and `content_hash`.

### Retention

Background prune runs daily (and on package load if it hasn't run today):

- All snapshots from the last **7 days** kept.
- Snapshots older than 7 days: keep one per hour for 30 days.
- Snapshots older than 30 days: keep one per day, indefinitely (until user
  prunes).
- Snapshots with `reason in ('saved', 'closed')` are **never** pruned.
- Snapshots referenced by a sidebar item (i.e. the latest snapshot for any
  tab_id currently in the tree) are **never** pruned.

User-facing settings:

- `RetentionEnabled` (default `true`)
- `MaxStorageMB` (default `2048`). When exceeded, prune more aggressively
  oldest-first regardless of the rules above, until under quota.

## Tab identity

Three layers, used in this order to associate a snapshot with previous history:

1. **`@id` from comment metadata** - authoritative if present.
2. **Content fingerprint** - SHA-256 of normalised content (whitespace
   collapsed, comment block stripped). If the fingerprint matches a recently
   seen tab (within last 14 days), inherit its `tab_id`.
3. **New tab** - generate a new `tab_id` and (per settings) inject `@id`.

This gives reliable continuity for tabs with `@id`, best-effort continuity for
tabs without, and graceful handling of brand-new tabs.

## Connection extraction

Best-effort. v1 parses the SSMS window title, which is typically formatted:

```
SQLQuery3.sql - SERVER.database (LOGIN (NN))*
```

Regex: `^.+? - ([^.]+)\.([^ ]+) \(`

If the title doesn't match, leave `server` and `database` NULL. Don't fail the
snapshot. A future version may use SSMS internal APIs via reflection.

## Privacy / safety

- Setting: `ServerAllowList` and `ServerDenyList` (lists of server-name
  patterns). When a tab's parsed server matches the deny list, no snapshots
  are written and no `@id` is injected. The sidebar shows the open tab with
  a "history disabled for this server" indicator.
- `-- @nosnapshot` directive disables history for that specific tab,
  regardless of server.
- A first-run dialog explains that the extension stores SQL text locally and
  asks the user to confirm or configure the deny list.

## Sidebar UI

The extension's primary UI is a **side panel** — one VS tool window
registered with `ToolWindowAttribute(Style = VsDockStyle.Tabbed,
Window = ToolWindowGuids80.SolutionExplorer)` so it docks alongside Solution
Explorer / Object Explorer by default. Users can drag it to any other dock
position; the position is persisted by the VS shell.

Title: "Tab Organiser".

Two top-level tabs in the tool window:

### "Tabs" tab

The default view of the side panel. Two switchable layouts via a toggle at
the top of the panel:

- **Tree** — folder structure derived from `@folder`.
- **List** — flat list of all tabs, sorted per the active sort mode.

```
[search box]                  [Tree | List]  [Sort ▾]  [⚙]
──────────────────────────────────────────────────────────
▾ 📁 Investigations
  ▾ 📁 PROD-1234
      📄 Find slow queries          ● *  #prod #performance
      📄 Lock analysis              ●    #prod
      📄 Old hypothesis                  #archive
▾ 📁 Migrations
    📄 v2.4 audit table             ● *  #migration
▾ 📁 Unfiled
    📄 SQLQuery7                    ●
    📄 SQLQuery3
──────────────────────────────────────────────────────────
[detail pane: rendered markdown of selected item]
```

Each row displays the item's tags inline as small chips after the name;
clicking a chip narrows the search to that tag (see Search syntax below).

State indicators on items:

- `●` = currently open in SSMS
- `*` = dirty (changes since last snapshot)
- (no marker) = closed, history only

Folder ordering: alphabetical, but `Unfiled` is always last.
Item ordering within a folder: by the active sort mode (default
**Most recent first**).

#### Sort modes

The `Sort ▾` dropdown offers:

- **Most recent first** *(default)* — by `tabs_latest.ts DESC`. In Tree
  view, this orders items within each folder; folders themselves are
  ordered by their newest contained item's timestamp.
- **Name (A–Z)** — by `tabs_latest.name COLLATE NOCASE ASC`.
- **Name (Z–A)** — descending equivalent.
- **Folder, then name** — pure alphabetical (the original v1 behaviour).

The chosen sort mode is persisted as `ui.tabsSortMode` in `settings.json`.

#### Search box

Single text box at the top of the panel. Filters the visible items as you
type (debounced 150ms). Case-insensitive. Search syntax:

| Token              | Matches                                                  |
|--------------------|----------------------------------------------------------|
| `foo`              | Substring of `name` **OR** any tag **OR** folder path.   |
| `name:foo`         | Substring of `name` only.                                |
| `#foo` or `tag:foo`| Exact tag match (case-insensitive).                      |
| `folder:foo/bar`   | Substring of folder path.                                |
| `desc:foo`         | Substring of `@desc`.                                    |
| `since:7d`         | `ts` within the last 7 days. Units: `m`, `h`, `d`, `w`.  |

Multiple tokens are AND-ed. `#a #b` matches items tagged with both `a` and
`b`. A bare leading `-` negates: `#prod -#archive`.

Implementation: parse the search string into a small AST, translate to a
parameterised SQL query against `tabs_latest` joined with `snapshot_tags`
on `latest_snapshot_id`. No LIKE on free-text columns larger than `name`/
`folder` — `@desc` is searched in-memory against the row set returned by
the structural filters.

The last 10 distinct searches are stored in `ui.searchHistory` and shown as
a dropdown when the search box is focused.

#### Context menu - item

- Open (default action / double-click)
- Open in new query window
- Show in history
- Copy path
- Copy id
- Reveal snapshot file

#### Context menu - folder

- Expand all
- Collapse all
- Copy folder path

(No rename/delete on folders - they're derived from comments. Editing the
comment renames the folder.)

#### Detail pane

Below the tree (resizable splitter). When an item is selected, shows:

- Name as heading
- Tags as chips
- Folder path as breadcrumb
- Server/database if known
- Rendered markdown of `@desc`
- Last snapshot time
- "Show all versions" link -> jumps to History tab filtered to this tab_id

Markdown rendering: **Markdig** in a `FlowDocumentScrollViewer`,
themed against `EnvironmentColors`.

#### Opening an item

- If the tab is currently open in SSMS: activate that document window.
- If closed: write the latest snapshot to a temp file and open it via
  `VS.Documents.OpenAsync(tempPath)`. The temp file is in
  `%LOCALAPPDATA%\AutoTabOrganiser\open\` and is cleaned up on package shutdown.

### "History" tab

Flat list of all snapshots, newest first. Columns: time, name, folder,
reason, size. Filterable by tab_id, folder, date range, reason. Selecting a
snapshot shows the SQL in a read-only preview pane with an "Open as new tab"
button.

## Theming

All UI must use VS theme brushes via `EnvironmentColors` resource keys
(`ToolWindowBackgroundBrushKey`, `ToolWindowTextBrushKey`,
`TreeViewBackgroundBrushKey`, etc.) and respond to theme changes without
restart.

## Settings

### Settings file location and persistence

The settings file lives at a fixed, user-scoped path that does **not** depend
on the snapshot storage location (so the user can move snapshots without
losing settings, and a fresh install still finds prior settings):

```
%APPDATA%\AutoTabOrganiser\settings.json
```

Notes:

- `%APPDATA%` (i.e. roaming) is used so that settings follow the user across
  machines under standard Windows roaming profiles. Snapshots stay local
  under `%LOCALAPPDATA%` (or the user's chosen `StorageLocation`) because
  they can be large.
- The file is read on package load. If absent or invalid JSON, defaults are
  used and the file is written on first change.
- Writes are atomic: write to `settings.json.tmp`, then `File.Replace`.
- A `schemaVersion` integer is stored at the top level. v1 = `1`. Future
  versions migrate forward on load.
- The settings file is the **single source of truth for `StorageLocation`**.
  Changing it via the Options page moves no data automatically; it changes
  where new snapshots are written and where the index is read from. A
  "Move existing snapshots" button on the Options page does the migration
  (copy + verify + delete source), with a confirmation dialog.

### Settings schema

JSON shape (with defaults shown):

```json
{
  "schemaVersion": 1,
  "storage": {
    "location": null,
    "maxStorageMB": 2048,
    "retentionEnabled": true
  },
  "snapshotting": {
    "editDebounceSeconds": 5,
    "flushIntervalSeconds": 60,
    "autoInjectId": true
  },
  "privacy": {
    "serverAllowList": [],
    "serverDenyList": [],
    "consentGiven": false,
    "consentTimestamp": null
  },
  "ui": {
    "lastSelectedTabId": null,
    "treeExpandedFolders": [],
    "detailPaneHeightPx": 240,
    "searchHistory": [],
    "tabsViewMode": "tree",
    "tabsSortMode": "recent"
  }
}
```

`storage.location = null` means "use the platform default"
(`%LOCALAPPDATA%\AutoTabOrganiser`). Any other value must be an absolute path
that exists and is writable; the Options page validates this before saving.

### Settings table

| Setting                       | Default     | Notes                                      |
|-------------------------------|-------------|--------------------------------------------|
| `storage.location`            | `null`      | Absolute path. `null` = platform default. Persisted across reinstalls. |
| `storage.maxStorageMB`        | 2048        | Quota for snapshots dir.                   |
| `storage.retentionEnabled`    | true        |                                            |
| `snapshotting.editDebounceSeconds` | 5      | Range 2-30                                 |
| `snapshotting.flushIntervalSeconds` | 60    |                                            |
| `snapshotting.autoInjectId`   | true        |                                            |
| `privacy.serverAllowList`     | `[]`        | If non-empty, only these servers snapshot. |
| `privacy.serverDenyList`      | `[]`        |                                            |
| `privacy.consentGiven`        | false       | Set by first-run consent dialog.           |
| `ui.detailPaneHeightPx`       | 240         | Splitter position; persisted on resize.    |
| `ui.treeExpandedFolders`      | `[]`        | Restored on tool-window open.              |
| `ui.tabsViewMode`             | `"tree"`    | `"tree"` or `"list"`.                      |
| `ui.tabsSortMode`             | `"recent"`  | `"recent"`, `"name-asc"`, `"name-desc"`, `"folder-name"`. |

UI for editing settings lives in a Tools > Options page under "Tab History"
plus the gear `⚙` button in the side panel header (which deep-links to the
Options page).

## Commands and key bindings

| Command                                | Default binding | Notes                          |
|----------------------------------------|-----------------|--------------------------------|
| Tab History: Show Tool Window          | (none)          | View menu                      |
| Tab History: Snapshot Now              | `Ctrl+Alt+H, S` | Force a snapshot, ignoring debounce |
| Tab History: Show This Tab's History   | `Ctrl+Alt+H, T` | History tab filtered to current tab_id |
| Tab History: Open Settings             | (none)          | Tools > Options shortcut       |

## Threading

Critical and easy to get wrong. Rules:

- All VS service access on UI thread. Use
  `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` before touching
  any `IVs*` interface.
- Text buffer change events fire on the UI thread but the snapshot work
  (hashing, file I/O, SQLite) must run on a background thread. Use
  `Task.Run` from the event handler with the captured text.
- Tree model updates from background threads must marshal back to the UI
  thread via the dispatcher.
- The pruner runs entirely on a background thread.

## Logging

Use `Microsoft.Extensions.Logging` (or a tiny home-grown equivalent) writing to
`%LOCALAPPDATA%\AutoTabOrganiser\logs\YYYY-MM-DD.log`. Levels: Debug, Info,
Warn, Error. Rotate daily, keep 14 days.

A "Diagnostics" entry in the tool window's gear menu opens the log folder.

## Project structure

```
AutoTabOrganiser.sln
src/
  AutoTabOrganiser/                       -- VSIX project
    AutoTabOrganiser.csproj
    source.extension.vsixmanifest
    AutoTabOrganiserPackage.cs            -- AsyncPackage entry point
    Commands/
      ShowToolWindowCommand.cs
      SnapshotNowCommand.cs
      ShowThisTabHistoryCommand.cs
    Tracking/
      DocumentTracker.cs                -- subscribes to VS doc events
      SnapshotPipeline.cs               -- debounce, hash, dedup
      ConnectionExtractor.cs            -- window-title parsing
    Metadata/
      MetadataParser.cs                 -- comment block -> Metadata
      MetadataWriter.cs                 -- @id injection
      Metadata.cs                       -- record type
    Storage/
      SnapshotStore.cs                  -- SQLite + file I/O
      Schema.sql                        -- embedded resource
      Pruner.cs
    Tree/
      TreeModel.cs                      -- INotifyPropertyChanged tree
      TreeNode.cs
    UI/
      ToolWindow.cs                     -- ToolWindowPane
      ToolWindowControl.xaml
      ToolWindowControl.xaml.cs
      Tabs/
        TabsView.xaml
        TabsViewModel.cs
      History/
        HistoryView.xaml
        HistoryViewModel.cs
      Detail/
        DetailPane.xaml
        MarkdownRenderer.cs
      Theming/
        VsBrushes.cs                    -- EnvironmentColors helpers
    Settings/
      SettingsStore.cs
      OptionsPage.cs
    Util/
      JoinableTaskExtensions.cs
      Hashing.cs
      PathSanitiser.cs
tests/
  AutoTabOrganiser.Tests/                 -- xUnit, no VS dependency
    MetadataParserTests.cs
    PathSanitiserTests.cs
    HashingTests.cs
    PrunerTests.cs                      -- against in-memory SQLite
```

## Key dependencies

- `Microsoft.VisualStudio.SDK` (17.x)
- `Community.VisualStudio.Toolkit.17`
- `Microsoft.Data.Sqlite` (or `System.Data.SQLite` if .NET Framework 4.7.2
  has issues with the .NET version - check at scaffolding time)
- `Markdig` for markdown rendering
- `Microsoft.Xaml.Behaviors.Wpf` (only if needed)
- xUnit + FluentAssertions for tests

## Build order (recommended slices for incremental development)

Each slice should compile, run, and be verifiable in experimental SSMS
(`/RootSuffix Exp`) before moving to the next.

### Slice 1: Hello World

- VSIX project that loads in SSMS 21.
- `AsyncPackage` that logs "Loaded" on startup.
- One command "Tab History: Hello" that shows a message box.

**Verification:** Launch experimental SSMS, see message box on command.

### Slice 2: Document tracking

- Subscribe to document opened/closed/saved events.
- Log each event with the document path/title.

**Verification:** Open and close SQL tabs, see log entries.

### Slice 3: Snapshot pipeline

- Capture text buffer on save/close.
- Compute hash, write to disk in `snapshots/...` structure.
- No metadata parsing yet, no SQLite, no dedup.

**Verification:** Save a SQL tab, see file appear in
`%LOCALAPPDATA%\AutoTabOrganiser\snapshots\...`.

### Slice 4: SQLite index + dedup

- Add the SQLite store and write index rows.
- Dedup by content hash.
- Add the edit debounce trigger.

**Verification:** Type in a tab, see periodic snapshots, identical content
not duplicated.

### Slice 5: Metadata parser + tag indexer

- Parse the comment block at the top of each tab (`@folder`, `@name`,
  `@desc`, `@nosnapshot`).
- Tag indexer: scan the **whole document**, tracking `--` and `/* */`
  comment state, and emit every `#word` found inside a comment.
- Populate `snapshots.folder`, `snapshots.name`, the `snapshot_tags` rows,
  and `tabs_latest` (with `tags_csv`) on every snapshot insert.
- Snapshot filenames now derive from the first non-blank, non-comment line
  (per the "Snapshot filename" rules above), with the documented fallbacks.
- No `@id` injection yet.

**Verification:** Add `#prod` to a comment in the middle of a long query,
save it, query `snapshot_tags` and `tabs_latest.tags_csv` — `prod` is
indexed. A `#temp` table reference outside any comment is **not** indexed.
File on disk is named after the first SQL line.

### Slice 6: `@id` injection

- Generate `tab_id` for new tabs.
- Inject `@id` line into the comment block (respecting cursor-safety rule).
- Use `tab_id` to associate snapshots.

**Verification:** Open a new tab with `@folder` only, save it, see `@id`
appear in the comment block. Close and reopen, history continues.

### Slice 7: Tool window with stub tree

- Create the tool window and command to show it.
- Display the tree as plain text (no styling), built from the SQLite index.
- Refresh on snapshot.

**Verification:** Open tool window, see tabs grouped by folder.

### Slice 8: Tree UI polish + search + sort

- Replace text dump with `TreeView` (and the alternate flat `ListView`).
- Theme against `EnvironmentColors`.
- State indicators (open/dirty).
- Inline tag chips on each row; click to add `#tag` to search.
- Context menu.
- Search box with the documented syntax (`name:`, `tag:`/`#`, `folder:`,
  `desc:`, `since:`, AND, leading `-` negation). Parser → SQL against
  `tabs_latest` + `snapshot_tags`.
- View toggle (Tree | List) and Sort dropdown (recent / name asc / name
  desc / folder+name); persist choices to `ui.tabsViewMode` and
  `ui.tabsSortMode`.
- Open-on-double-click.

**Verification:**
1. `#prod` filters to prod-tagged tabs only.
2. `name:slow #prod` AND-combines.
3. `since:1d` shows only tabs modified in the last day.
4. Switch to List + "Most recent first" sort, see most recently edited tab
   at the top.
5. Restart SSMS — view mode and sort mode are remembered.

### Slice 9: Markdown detail pane

- Parse `@desc` multi-line.
- Render with Markdig.
- Wire to tree selection.

**Verification:** Add markdown description to a tab, see it rendered.

### Slice 10: History tab

- Second tab in tool window.
- Flat list with filters.
- Read-only preview + "open as new tab".

**Verification:** Browse history, restore an old version.

### Slice 11: Retention

- Implement the prune rules.
- Run on package load and daily.
- Quota enforcement.

**Verification:** Manually create old snapshots, run prune, see them removed
according to rules.

### Slice 12: Settings + privacy

- Implement the `settings.json` schema and atomic read/write at
  `%APPDATA%\AutoTabOrganiser\settings.json`.
- Options page bound to the schema, including `StorageLocation` with path
  validation and a "Move existing snapshots" action.
- Persist UI state (`detailPaneHeightPx`, `treeExpandedFolders`, etc.) on
  change and restore on tool-window open.
- Server allow/deny lists, with snapshot suppression.
- First-run consent dialog; sets `privacy.consentGiven`.

**Verification:** Change `StorageLocation`, restart SSMS, confirm new
snapshots write to the new path and settings persist. Set a deny list, see
snapshots not written for that server. Resize the detail pane, reopen the
tool window, splitter position restored.

## Testing strategy

- **Unit tests** for `MetadataParser`, `PathSanitiser`, `Hashing`, and
  `Pruner`. These are the parts with no VS dependency and the most logic.
  These should achieve high coverage.
- **Manual integration testing** in experimental SSMS for everything else.
  There is no automated way to drive SSMS reliably.
- A scratch `.sql` file in the repo containing all the metadata edge cases
  (no comments, only `@folder`, `@desc` with weird indentation, very long
  description, etc.) for manual regression testing.

## Out of scope (note for future versions)

- v1.1: dedicated markdown preview tool window for the active tab.
- v1.2: cross-machine sync via the user's choice of OneDrive/Dropbox folder.
- v2.0: integration with git repos - tabs that live in a watched folder are
  treated as version-controlled and the extension can show "differences from
  HEAD" in the detail pane.
- v2.0: scheduled queries via `@schedule: 0 9 * * 1-5` in metadata.

---

## Notes for the implementing agent

1. **Verify before assuming APIs exist.** VS extensibility documentation is
   thin and APIs change between shell versions. Before relying on any
   `IVs*` interface or Community Toolkit method, check it against the actual
   loaded assembly version in your project. Where uncertain, write a tiny
   probe and run it in experimental SSMS rather than guess.
2. **Threading mistakes are silent.** A missed
   `SwitchToMainThreadAsync()` may not throw - it may just hang or no-op.
   Treat any "the UI doesn't update" symptom as a threading suspect first.
3. **Do not skip the slice-by-slice build order.** Each slice exists because
   the next one depends on it being verifiable. Skipping ahead means
   debugging compounded failures.
4. **Untitled tabs do not have a real file path.** Code that does
   `File.ReadAllText(documentPath)` will fail for them. Always read from the
   text buffer.
5. **Inject `@id` carefully.** Editing the buffer while the user is typing
   in the affected region is jarring. Honour the cursor-safety rule.
6. **The user's SQL is sensitive data.** Default to local-only storage.
   Never log full SQL contents at info level - log lengths and hashes only.
   Full content goes to debug only, and debug logging is off by default.
