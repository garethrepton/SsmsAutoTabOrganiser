using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoTabOrganiser.UI.Diff
{
    /// <summary>
    /// Modal popup showing a coloured unified diff. Shared by the git-diff button in the
    /// Stored Queries panel and the snapshot-history timeline's Diff action. Each line gets
    /// a Run with the appropriate foreground brush; a single FlowDocument with inline
    /// Runs + LineBreaks keeps layout cost roughly proportional to char-count rather than
    /// control-count, which matters for big diffs.
    /// </summary>
    internal static class DiffDialog
    {
        public static void Show(string title, string diff)
        {
            var win = new Window
            {
                Title = title ?? "diff",
                Width = 900, Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var viewer = new FlowDocumentScrollViewer
            {
                Document = BuildDiffDocument(diff),
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

            // Esc closes (also picked up by IsCancel on Close, but the explicit handler covers
            // when focus is on the document viewer).
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { win.Close(); e.Handled = true; }
            };

            win.ShowDialog();
        }

        public static FlowDocument BuildDiffDocument(string diff)
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
