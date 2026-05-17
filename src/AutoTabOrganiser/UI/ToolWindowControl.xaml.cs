using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.UI.Detail;
using AutoTabOrganiser.UI.ViewModels;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI
{
    /// <summary>
    /// Tool window view. Holds the <see cref="ToolWindowViewModel"/> as its DataContext and
    /// keeps only the bits of glue that genuinely belong to the view: the detail-pane host,
    /// dialog-style modal popups (tag picker, commit message prompt), and a handful of
    /// keyboard/mouse handlers that are awkward to express purely in XAML.
    /// </summary>
    internal partial class ToolWindowControl : UserControl
    {
        private ToolWindowViewModel _vm;
        private DetailPane _detailPane;

        public ToolWindowControl()
        {
            InitializeComponent();
            _detailPane = new DetailPane();
            DetailHost.Content = _detailPane;
            // When the tool window is closed (especially a multi-instance one), tear down VM
            // resources — FileSystemWatchers and DispatcherTimers — so they don't leak. Unloaded
            // fires reliably when the WPF visual is torn down from the SSMS frame.
            Unloaded += (s, e) =>
            {
                try { _vm?.Dispose(); } catch { }
            };
        }

        public void Initialise(SnapshotStore store, Func<string, Task> openTabId, Action onSettingsClick,
                               Action onSnapshotNow, Action onQuickSwitcher, Action onTagConfig,
                               Action onNewView,
                               Logger log, SettingsStore settings, string sortMode)
        {
            _vm = new ToolWindowViewModel(store, settings, log,
                openTabId: openTabId,
                openSettings: onSettingsClick,
                snapshotNow: onSnapshotNow,
                quickSwitcher: onQuickSwitcher,
                tagConfig: onTagConfig,
                newView: onNewView,
                openAsNewSnapshot: r => OpenSnapshotHandler != null ? OpenSnapshotHandler(r) : Task.CompletedTask,
                showTagPicker: ShowTagPickerDialog,
                promptCommitMessage: PromptForCommitMessage,
                showGitDiff: ShowGitDiffDialog,
                confirm: ShowConfirmDialog,
                showInfo: null,
                onSavePromptShown: () =>
                {
                    SaveNameBox.SelectAll();
                    SaveNameBox.Focus();
                },
                dispatcher: Dispatcher);

            _vm.SelectionChanged += (s, t) =>
            {
                if (t == null) _detailPane.Clear(); else _detailPane.Show(t);
            };

            DataContext = _vm;
            _vm.Initialise(sortMode);
        }

        // ---- public surface used by the package ----

        public Func<SnapshotRecord, Task> OpenSnapshotHandler { get; set; }

        public string SortMode => _vm?.SortMode ?? "recent";

        public void RefreshTabs() => _vm?.RefreshAll();
        public void RefreshRecent() => _vm?.RefreshRecent();
        public void RefreshSnippets() => _vm?.RefreshStoredQueries();
        public void ShowInfo(string message) => _vm?.ShowInfoExternal(message);

        /// <summary>Push the currently-active SSMS tab id to the VM so it pins to the top of RECENT.</summary>
        public void SetActiveTabId(string tabId)
        {
            if (_vm != null) _vm.ActiveTabId = tabId;
        }

        // ---- list interactions: double-click opens, single-click drives the detail pane ----

        private void OnList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var lb = sender as ListBox;
            if (lb?.SelectedItem is TabRowViewModel vm) _vm?.OpenTabCommand.Execute(vm);
        }

        private void OnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var lb = sender as ListBox;
            if (_vm == null || lb?.SelectedItem == null) return;
            if (lb.SelectedItem is TabRowViewModel vm) _vm.Selected = vm;
        }

        private void OnStoredQueries_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is StoredQueryRowViewModel vm)
                _vm?.OpenTabById(vm.Source?.TabId);
        }

        // ---- search ----

        private void OnSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_vm?.SearchText))
            {
                _vm.SearchText = "";
                e.Handled = true;
            }
        }

        // Search-syntax help: toggle the popover open. StaysOpen=False on the Popup gives us
        // click-outside dismissal for free; OnSearchHelpPopup_KeyDown handles Esc.
        private void OnSearchHelp_Click(object sender, RoutedEventArgs e)
        {
            if (SearchHelpPopup == null) return;
            SearchHelpPopup.IsOpen = !SearchHelpPopup.IsOpen;
        }

        private void OnSearchHelpPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && SearchHelpPopup != null)
            {
                SearchHelpPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        // ---- stored-queries commit message: Ctrl+Enter triggers Commit All ----

        private void OnCommitMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _vm?.CommitAllStoredQueriesCommand.Execute(null);
                e.Handled = true;
            }
        }

        // ---- save prompt Esc-to-cancel ----

        private void OnSaveNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { _vm?.CancelSaveScriptsCommand.Execute(null); e.Handled = true; }
        }

        // ---- "Pin tag" submenu — populated dynamically from the right-clicked row's tags. ----

        private void OnPinTagSubmenuOpened(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            if (mi == null || _vm == null) return;
            mi.Items.Clear();

            var t = ContextTargetFrom(mi);
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
                    IsChecked = _vm.CurrentPinnedTags.Contains(tag, StringComparer.OrdinalIgnoreCase),
                    Command = _vm.TogglePinTagCommand,
                    CommandParameter = tag
                };
                mi.Items.Add(item);
            }
        }

        private static TabSummary ContextTargetFrom(MenuItem mi)
        {
            // Walk up to the ContextMenu, then read DataContext from PlacementTarget (ListBoxItem).
            DependencyObject d = mi;
            while (d != null && !(d is ContextMenu)) d = LogicalTreeHelper.GetParent(d) ?? System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (!(d is ContextMenu cm)) return null;
            if (cm.PlacementTarget is ListBoxItem lbi)
            {
                if (lbi.DataContext is TabRowViewModel vm) return vm.Source;
                if (lbi.DataContext is StoredQueryRowViewModel svm) return svm.Source;
                if (lbi.DataContext is TabSummary t) return t;
            }
            return null;
        }

        // ---- modal dialogs the VM delegates to the view ----

        private static List<string> ShowTagPickerDialog(List<string> allTags, List<string> pinned)
        {
            var win = new Window
            {
                Title = "Pin tags", Width = 280, Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow
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
            foreach (var p in pinned)
                if (!allMerged.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))) allMerged.Add(p);
            allMerged.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var t in allMerged)
            {
                var cb = new CheckBox
                {
                    Content = "#" + t,
                    Tag = t,
                    IsChecked = pinned.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase))
                };
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

        /// <summary>
        /// Modal Yes/No confirmation. Returns true when the user picks Yes; Enter triggers Yes,
        /// Esc/Cancel triggers No.
        /// </summary>
        private static bool ShowConfirmDialog(string title, string message)
        {
            var win = new Window
            {
                Title = title ?? "Confirm",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(text, 0);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var yes = new Button { Content = "Delete", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            btnPanel.Children.Add(yes);
            btnPanel.Children.Add(no);
            Grid.SetRow(btnPanel, 1);

            grid.Children.Add(text);
            grid.Children.Add(btnPanel);
            win.Content = grid;

            bool result = false;
            yes.Click += (s, e) => { result = true; win.DialogResult = true; };
            return win.ShowDialog() == true && result;
        }

        private static string PromptForCommitMessage(string fileName)
        {
            var win = new Window
            {
                Title = "git commit — " + fileName,
                Width = 460, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Commit message:", Margin = new Thickness(8) };
            Grid.SetRow(label, 0);
            var tb = new TextBox
            {
                Margin = new Thickness(8, 0, 8, 8), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Text = "Update " + System.IO.Path.GetFileNameWithoutExtension(fileName)
            };
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

        /// <summary>
        /// Modal popup showing a coloured git diff. Each line gets a Run with the appropriate
        /// foreground brush; we use a single FlowDocument with inline Runs + LineBreaks rather
        /// than per-line TextBlocks so the layout cost stays roughly proportional to char-count
        /// rather than control-count, which matters for big diffs.
        /// </summary>
        private static void ShowGitDiffDialog(string fileName, string diff, GitFileStatus status)
        {
            var win = new Window
            {
                Title = "git diff — " + fileName + "  [" + status + "]",
                Width = 900, Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var doc = BuildDiffDocument(diff);
            var viewer = new FlowDocumentScrollViewer
            {
                Document = doc,
                Margin = new Thickness(0),
                IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(viewer, 0);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 80 };
            close.Click += (s, e) => win.Close();
            btnPanel.Children.Add(close);
            Grid.SetRow(btnPanel, 1);

            grid.Children.Add(viewer);
            grid.Children.Add(btnPanel);
            win.Content = grid;

            // Esc closes (also picked up by IsCancel on Close, but explicit handler covers
            // when focus is on the document viewer).
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { win.Close(); e.Handled = true; }
            };

            win.ShowDialog();
        }

        private static FlowDocument BuildDiffDocument(string diff)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Lucida Console"),
                FontSize = 12,
                PagePadding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC))
            };

            var added    = new SolidColorBrush(Color.FromRgb(0x6F, 0xC2, 0x76));
            var removed  = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
            var hunk     = new SolidColorBrush(Color.FromRgb(0x6D, 0xA8, 0xE2));
            var header   = new SolidColorBrush(Color.FromRgb(0xC5, 0x9E, 0x6E));
            var muted    = new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0x96));

            var para = new Paragraph { Margin = new Thickness(0) };
            doc.Blocks.Add(para);

            if (string.IsNullOrEmpty(diff))
            {
                para.Inlines.Add(new Run("(empty diff)") { Foreground = muted });
                return doc;
            }

            // Cap to 5000 lines so a runaway diff doesn't freeze the layout pass.
            var lines = diff.Replace("\r\n", "\n").Split('\n');
            int max = 5000;
            int count = Math.Min(lines.Length, max);
            for (int i = 0; i < count; i++)
            {
                var line = lines[i];
                Brush fg;
                if (line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") || line.StartsWith("index "))
                    fg = header;
                else if (line.StartsWith("@@"))
                    fg = hunk;
                else if (line.Length > 0 && line[0] == '+')
                    fg = added;
                else if (line.Length > 0 && line[0] == '-')
                    fg = removed;
                else
                    fg = doc.Foreground;

                para.Inlines.Add(new Run(line) { Foreground = fg });
                para.Inlines.Add(new LineBreak());
            }
            if (lines.Length > max)
            {
                para.Inlines.Add(new Run($"… (truncated; {lines.Length - max} more lines)") { Foreground = muted });
                para.Inlines.Add(new LineBreak());
            }
            return doc;
        }

    }
}
