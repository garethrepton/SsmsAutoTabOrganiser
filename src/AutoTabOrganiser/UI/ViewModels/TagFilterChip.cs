using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>
    /// Clickable tag chip used in the unified search/recent strip. Differs from the read-only
    /// <see cref="TagChip"/> by adding a toggle command and an active-state flag the view binds
    /// for a highlighted-when-applied look.
    /// </summary>
    internal sealed class TagFilterChip : INotifyPropertyChanged
    {
        public string Text { get; }
        public Brush Brush { get; }
        public Brush Foreground { get; }
        public ICommand ToggleCommand { get; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive == value) return; _isActive = value; Notify(); }
        }

        public TagFilterChip(string text, Brush brush, ICommand toggleCommand)
        {
            Text = text;
            Brush = brush;
            Foreground = ContrastingForeground(brush);
            ToggleCommand = toggleCommand;
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

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
