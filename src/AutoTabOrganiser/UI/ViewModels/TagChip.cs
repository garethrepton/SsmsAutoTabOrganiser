using System.Windows.Media;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>One coloured tag pill for the side panel rows and the detail pane.</summary>
    internal sealed class TagChip
    {
        public string Text { get; }
        public Brush Brush { get; }
        public Brush Foreground { get; }

        public TagChip(string text, Brush brush)
        {
            Text = text;
            Brush = brush;
            Foreground = ContrastingForeground(brush);
        }

        private static Brush ContrastingForeground(Brush b)
        {
            if (b is SolidColorBrush sb)
            {
                var c = sb.Color;
                var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                return lum > 0.6 ? Brushes.Black : Brushes.White;
            }
            return Brushes.White;
        }
    }
}
