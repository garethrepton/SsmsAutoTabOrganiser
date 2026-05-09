using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        }

        public void Initialise(SnapshotStore store, Func<string, Task> openTabId, Action onSettingsClick,
                               Logger log, SettingsStore settings, string viewMode, string sortMode)
        {
            _vm = new ToolWindowViewModel(store, settings, log,
                openTabId: openTabId,
                openSettings: onSettingsClick,
                openAsNewSnapshot: r => OpenSnapshotHandler != null ? OpenSnapshotHandler(r) : Task.CompletedTask,
                showTagPicker: ShowTagPickerDialog,
                promptCommitMessage: PromptForCommitMessage,
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

        public string ViewMode => "list";
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

        // ---- storage-path label click → same as the Open button ----

        private void OnStoragePath_Click(object sender, MouseButtonEventArgs e)
        {
            _vm?.OpenStorageCommand.Execute(null);
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

    }
}
