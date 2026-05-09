using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoTabOrganiser.Editor;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.UI.TagConfig
{
    /// <summary>
    /// Tabbed configuration dialog covering both tag colours and the keyword-driven auto-tag
    /// rules (formerly two separate concerns; the rules side previously had no UI at all and
    /// was JSON-only). Cancel discards both pending edits; OK persists both atomically via
    /// <see cref="SettingsStore.Mutate"/>.
    /// </summary>
    internal partial class TagConfigWindow : Window
    {
        private readonly SettingsStore _settings;

        // Working copies — Cancel just drops the window, both lists go away with it.
        private readonly Dictionary<string, string> _workingOverrides;
        private readonly ObservableCollection<RuleRow> _workingRules;

        private TagConfigWindow(SnapshotStore store, SettingsStore settings)
        {
            _settings = settings;
            InitializeComponent();

            var loaded = _settings.Load();

            // ----- COLOURS -----
            _workingOverrides = new Dictionary<string, string>(
                loaded.Ui?.TagColours ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            var allTags = (store?.GetAllTags() ?? new List<string>())
                .Concat(_workingOverrides.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);

            var tagRows = new ObservableCollection<TagRow>();
            foreach (var t in allTags)
            {
                _workingOverrides.TryGetValue(t, out var hex);
                tagRows.Add(new TagRow(t, hex));
            }
            TagsList.ItemsSource = tagRows;

            // ----- RULES -----
            _workingRules = new ObservableCollection<RuleRow>();
            foreach (var r in loaded.Snapshotting?.AutoTagRules ?? new List<AutoTagRule>())
            {
                _workingRules.Add(new RuleRow(
                    r?.Match ?? "",
                    string.Join(", ", r?.Tags ?? new List<string>())));
            }
            RulesList.ItemsSource = _workingRules;
        }

        public static void Show(SnapshotStore store, SettingsStore settings, Window owner)
        {
            var win = new TagConfigWindow(store, settings)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            win.ShowDialog();
        }

        // ---- colours-tab actions ----

        private void OnPick_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as TagRow;
            if (row == null) return;

            // System.Windows.Forms.ColorDialog is the simplest reliable colour picker
            // available on .NET Framework 4.7.2 — no third-party dependency.
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                if (TryParseHex(row.Hex, out var current))
                    dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                dlg.FullOpen = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                var picked = dlg.Color;
                var hex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                row.Hex = hex;
                _workingOverrides[row.Tag] = hex;
            }
        }

        private void OnReset_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as TagRow;
            if (row == null) return;
            _workingOverrides.Remove(row.Tag);
            row.Hex = null;
        }

        // ---- rules-tab actions ----

        private void OnAddRule_Click(object sender, RoutedEventArgs e)
        {
            _workingRules.Add(new RuleRow("", ""));
            // Scroll the new row into view and focus the Match column for fast entry.
            RulesList.ScrollIntoView(_workingRules[_workingRules.Count - 1]);
        }

        private void OnRemoveRule_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as RuleRow;
            if (row != null) _workingRules.Remove(row);
        }

        // ---- OK / Cancel ----

        private void OnOk_Click(object sender, RoutedEventArgs e)
        {
            // Colours: drop empty overrides so they fall back to the palette.
            var cleanColours = _workingOverrides
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // Rules: drop rows with no Match string. Tags are split on commas, trimmed, leading
            // # stripped. A row with a Match but no Tags is kept (matches without applying tags
            // is a no-op, but the user might be in the middle of editing).
            var cleanRules = _workingRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Match))
                .Select(r => new AutoTagRule
                {
                    Match = r.Match.Trim(),
                    Tags = (r.TagsCsv ?? "")
                        .Split(',')
                        .Select(s => s.Trim().TrimStart('#').Trim())
                        .Where(s => s.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .ToList();

            _settings.Mutate(s =>
            {
                if (s.Ui == null) s.Ui = new UiSettings();
                s.Ui.TagColours = cleanColours;
                if (s.Snapshotting == null) s.Snapshotting = new SnapshottingSettings();
                s.Snapshotting.AutoTagRules = cleanRules;
            });
            DialogResult = true;
        }

        private void OnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ---- helpers ----

        private static bool TryParseHex(string hex, out Color c)
        {
            c = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6) return false;
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
            c = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            return true;
        }

        // ---- view-model rows ----

        private sealed class TagRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            public string Tag { get; }

            private string _hex;
            /// <summary>Current override hex (e.g. "#A1B2C3") or null when no override is set.</summary>
            public string Hex
            {
                get => _hex;
                set
                {
                    if (_hex == value) return;
                    _hex = value;
                    Notify(nameof(Hex));
                    Notify(nameof(HexLabel));
                    Notify(nameof(Swatch));
                }
            }

            public TagRow(string tag, string hex) { Tag = tag; _hex = hex; }

            public string HexLabel => string.IsNullOrEmpty(Hex) ? "(auto)" : Hex.ToUpperInvariant();

            /// <summary>Brush rendered in the swatch column. Picks up live changes via PropertyChanged.</summary>
            public Brush Swatch => TagColourResolver.Resolve(
                Tag,
                string.IsNullOrEmpty(Hex)
                    ? null
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Tag] = Hex });

            private void Notify([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private sealed class RuleRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private string _match;
            public string Match
            {
                get => _match;
                set { if (_match == value) return; _match = value; Notify(nameof(Match)); }
            }

            private string _tagsCsv;
            public string TagsCsv
            {
                get => _tagsCsv;
                set { if (_tagsCsv == value) return; _tagsCsv = value; Notify(nameof(TagsCsv)); }
            }

            public RuleRow(string match, string tagsCsv) { _match = match; _tagsCsv = tagsCsv; }

            private void Notify([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
