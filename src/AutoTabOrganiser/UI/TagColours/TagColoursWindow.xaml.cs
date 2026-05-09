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

namespace AutoTabOrganiser.UI.TagColours
{
    /// <summary>
    /// Tools menu dialog: lists every tag the store knows about with its current colour
    /// (override or auto-derived). Lets the user pick a custom hex per tag, or reset back
    /// to the palette default. Persists overrides to <c>settings.ui.tagColours</c>.
    /// </summary>
    internal partial class TagColoursWindow : Window
    {
        private readonly SettingsStore _settings;
        private readonly Dictionary<string, string> _workingOverrides;

        private TagColoursWindow(SnapshotStore store, SettingsStore settings)
        {
            _settings = settings;
            InitializeComponent();

            // Snapshot the existing overrides so Cancel reverts cleanly.
            var loaded = _settings.Load();
            _workingOverrides = new Dictionary<string, string>(
                loaded.Ui?.TagColours ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            var allTags = (store?.GetAllTags() ?? new List<string>())
                .Concat(_workingOverrides.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);

            var rows = new ObservableCollection<TagRow>();
            foreach (var t in allTags)
            {
                _workingOverrides.TryGetValue(t, out var hex);
                rows.Add(new TagRow(t, hex));
            }
            TagsList.ItemsSource = rows;
        }

        public static void Show(SnapshotStore store, SettingsStore settings, Window owner)
        {
            var win = new TagColoursWindow(store, settings)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            win.ShowDialog();
        }

        // ---- per-row actions ----

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

        // ---- OK / Cancel ----

        private void OnOk_Click(object sender, RoutedEventArgs e)
        {
            // Only persist non-empty overrides; empty entries fall back to the palette.
            var clean = _workingOverrides
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            _settings.Mutate(s =>
            {
                if (s.Ui == null) s.Ui = new UiSettings();
                s.Ui.TagColours = clean;
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

        // ---- view-model row ----

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
    }
}
