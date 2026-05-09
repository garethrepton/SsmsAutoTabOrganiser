# AutoTabOrganiser

A small SSMS extension that auto-snapshots every open query tab and gives
you a sidebar with search, pinned tag sections, and a tiny source-control
panel for the queries you save to disk.

## ⚠️ Disclaimer

I built this for myself and **I haven't even used it properly yet**.
There's no test coverage on the SSMS-loaded code path, no telemetry, no
beta testers. If it eats a tab, corrupts a file, or bricks SSMS startup,
I'd like to hear about it but I'm making no promises. Use it on a machine
where you don't mind the occasional reinstall, and **keep your own
backups of any query you care about** until you've watched this thing
behave for a while.

It only targets **SSMS 21+** on **Windows 10/11**.

## Install

1. Build Release (`dotnet build -c Release` from the repo root) or
   download a `.vsix` from a release.
2. Close SSMS.
3. Double-click `AutoTabOrganiser.vsix`, or run
   `VSIXInstaller.exe /quiet AutoTabOrganiser.vsix` against SSMS's
   `Common7\IDE\VSIXInstaller.exe`.
4. Launch SSMS. The tool window shows up under
   **View → Other Windows → Auto Tab Organiser**.

## How to use it

### Open the panel

`View → Other Windows → Auto Tab Organiser`. It's a normal VS tool window
— dock it wherever you'd dock Solution Explorer.

### Snapshots happen automatically

Every open `.sql` tab is debounced-snapshotted on edit, save, and close.
There's no Save button to click. Snapshots live under
`%LOCALAPPDATA%\AutoTabOrganiser` and are indexed in SQLite for fast
search.

### Tag your tabs with a comment header

The first contiguous comment block at the top of a tab is parsed for
metadata. The keys you'll use most:

```sql
-- @name:   Investigate slow CTE
-- @folder: Investigations/PROD-1234
-- @tags:   slow, cte, prod-1234
-- @desc:   Reproduces the regression seen on 2026-05-08.
SELECT ...
```

`@folder` drives the sidebar tree. `@tags` lets you pin sections
(see below). `@desc` accepts Markdown and renders in the detail pane.

### Search

The search box at the top accepts:

- bare words → full-text search across the snapshot content
- `name:foo` → match the tab name
- `#tag` / `-#tag` → include / exclude tags
- `folder:Investigations` → match folder
- `desc:slow` → match description text
- `since:7d` → last 7 days only
- Esc → clears the search

### Pinned tag sections

Click **Pin tag**, pick one or more tags, and each gets its own collapsible
section listing every tab carrying that tag. Useful for live "buckets"
like `#prod`, `#wip`, `#ready-to-ship`.

### Stored Queries (source control panel)

When you "Save to scripts folder…" from a tab's hover button or context
menu, the tab gets written as a `.sql` file under your configured Stored
Queries folder. If that folder is inside a git repo, the **Source Control
— Stored Queries** section lights up:

- One row per file with uncommitted changes (modified / untracked / staged).
- Per-row buttons:
  - **Diff** — modal popup with a coloured `git diff HEAD --` against the file.
  - **Stage** — `git add` the file.
  - **Commit** — auto-message commit (uses the message in the textbox if
    you've typed one, otherwise `Update <name>`).
- **Commit All** — commits every dirty stored-query file with the textbox
  message. `Ctrl+Enter` from the textbox is a shortcut.
- **Open** — opens the Stored Queries folder in Explorer.
- **Terminal** — opens a terminal in the Stored Queries folder
  (Windows Terminal → PowerShell → cmd, in that order).

The panel updates automatically when files change on disk and when you
run git commands externally — there's a `FileSystemWatcher` on the
folder and on each repo's `.git` directory.

### Quick switcher

`Ctrl+P` opens a quick-switcher list of recent tabs. Type to filter,
Enter to open. Acts like the same shortcut in VS Code / Sublime.

### Right-click on any row

Open in current / new tab, copy id, copy file path, reveal snapshot,
save to scripts folder, pin a tag from the row, and a Git submenu with
add / commit / status for any tab whose latest snapshot is on disk.

## Settings

`View → Other Windows → Auto Tab Organiser`, then click the gear icon at
the top-right of the search row. Lets you change the snapshot retention,
the Stored Queries folder, the recent-tab count, and the per-tag colour
palette.

Settings are persisted to `%APPDATA%\AutoTabOrganiser\settings.json`.

## Where things live

| Thing | Path |
|---|---|
| Snapshots | `%LOCALAPPDATA%\AutoTabOrganiser\` |
| Settings | `%APPDATA%\AutoTabOrganiser\settings.json` |
| Logs | `%APPDATA%\AutoTabOrganiser\logs\YYYY-MM-DD.log` |
| Installed VSIX | `%LOCALAPPDATA%\Microsoft\SSMS\22.*\Extensions\<random>\` |

If something goes wrong, the daily log is the first place to look.

## Bug reports

Open an issue with the relevant tail of the log. SSMS version, OS
version, and what you were doing immediately before the problem all
help.
