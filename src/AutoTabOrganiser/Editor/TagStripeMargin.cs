using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Settings;

namespace AutoTabOrganiser.Editor
{
    /// <summary>
    /// A thin coloured stripe down the left edge of the editor showing the document's tags.
    /// Stripes are stacked vertically so multiple tags are all visible. Tags come from the
    /// document's comment block via <see cref="MetadataParser"/>; colours come from
    /// <see cref="TagColourResolver"/>.
    /// </summary>
    internal sealed class TagStripeMargin : Canvas, IWpfTextViewMargin
    {
        public const string MarginName = "AutoTabOrganiser.TagStripe";
        private const double StripeWidth = 6.0;

        private readonly IWpfTextView _view;
        private readonly DispatcherTimer _debounce;
        private readonly SettingsStore _settings;
        private bool _disposed;

        public TagStripeMargin(IWpfTextView view, SettingsStore settings)
        {
            _view = view;
            _settings = settings;
            Width = StripeWidth;
            Background = Brushes.Transparent;
            ToolTip = null;

            _debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Repaint(); };

            _view.TextBuffer.Changed += OnBufferChanged;
            _view.ViewportHeightChanged += OnViewportChanged;
            _view.Closed += (s, e) => Dispose();

            // First paint after layout is established.
            Loaded += (s, e) => Repaint();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnViewportChanged(object sender, EventArgs e) => Repaint();

        private void Repaint()
        {
            if (_disposed) return;

            var s = SafeLoadSettings();
            if (s == null || s.Ui == null || !s.Ui.TagStripeEnabled)
            {
                Children.Clear();
                Visibility = Visibility.Collapsed;
                return;
            }
            Visibility = Visibility.Visible;

            var text = SafeSnapshotText();
            var tags = new List<string>();
            try { MetadataParser.ExtractTagsFromAllComments(text, tags); }
            catch { /* parser is defensive but be paranoid in the editor */ }

            Children.Clear();
            if (tags.Count == 0) return;

            var height = ActualHeight;
            if (height <= 0) return;

            // One stripe per tag, equal-height segments stacked top-to-bottom.
            double segH = Math.Max(2.0, height / tags.Count);
            for (int i = 0; i < tags.Count; i++)
            {
                var brush = TagColourResolver.Resolve(tags[i], s.Ui.TagColours);
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = StripeWidth,
                    Height = (i == tags.Count - 1) ? Math.Max(2.0, height - segH * i) : segH,
                    Fill = brush,
                    ToolTip = "#" + tags[i]
                };
                SetLeft(rect, 0);
                SetTop(rect, segH * i);
                Children.Add(rect);
            }
        }

        private string SafeSnapshotText()
        {
            try { return _view.TextBuffer.CurrentSnapshot.GetText(); }
            catch { return string.Empty; }
        }

        private AppSettings SafeLoadSettings()
        {
            try { return _settings.Load(); }
            catch { return null; }
        }

        // ---- IWpfTextViewMargin ----
        public FrameworkElement VisualElement => this;
        public double MarginSize => StripeWidth;
        public bool Enabled => !_disposed;

        public ITextViewMargin GetTextViewMargin(string marginName)
            => string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _view.TextBuffer.Changed -= OnBufferChanged; } catch { }
            try { _view.ViewportHeightChanged -= OnViewportChanged; } catch { }
            _debounce.Stop();
        }
    }
}
