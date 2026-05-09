using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>
    /// One row in the SOURCE CONTROL — STORED QUERIES section. A tab whose stored-query
    /// file (saved to the user's Stored Queries folder) has uncommitted git changes —
    /// modified, untracked, or staged.
    /// </summary>
    /// <remarks>
    /// <see cref="Status"/> is mutable with INPC so optimistic updates can mutate the
    /// existing instance instead of replacing it in the ObservableCollection. Replacing
    /// fires CollectionChanged.Replace which makes the ListBox recreate the ListBoxItem;
    /// during that recreation, button hit-testing on adjacent rows can briefly fail and
    /// clicks get lost. Mutating in place avoids the ListBoxItem rebuild entirely.
    /// </remarks>
    internal sealed class StoredQueryRowViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public TabSummary Source { get; }
        public string FilePath { get; }

        private GitFileStatus _status;
        public GitFileStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                Notify(nameof(Status));
                Notify(nameof(Letter));
                Notify(nameof(StatusName));
                Notify(nameof(LetterBrush));
            }
        }

        public StoredQueryRowViewModel(TabSummary source, GitFileStatus status, string path)
        {
            Source = source;
            _status = status;
            FilePath = path;
        }

        public string FileName => string.IsNullOrEmpty(FilePath)
            ? (Source.Name ?? "(unnamed)")
            : Path.GetFileName(FilePath);

        public string Letter
        {
            get
            {
                switch (_status)
                {
                    case GitFileStatus.Modified:  return "M";
                    case GitFileStatus.Untracked: return "U";
                    case GitFileStatus.Staged:    return "A";
                    default: return " ";
                }
            }
        }

        public string StatusName
        {
            get
            {
                switch (_status)
                {
                    case GitFileStatus.Modified:  return "Modified";
                    case GitFileStatus.Untracked: return "Untracked";
                    case GitFileStatus.Staged:    return "Staged (added)";
                    default: return _status.ToString();
                }
            }
        }

        public Brush LetterBrush
        {
            get
            {
                switch (_status)
                {
                    case GitFileStatus.Modified:
                        return new SolidColorBrush(Color.FromRgb(0xE2, 0xC0, 0x8D)); // amber
                    case GitFileStatus.Untracked:
                        return new SolidColorBrush(Color.FromRgb(0x73, 0xC9, 0x91)); // green
                    case GitFileStatus.Staged:
                        return new SolidColorBrush(Color.FromRgb(0x81, 0xB8, 0x8B)); // staged-green
                    default:
                        return Brushes.Gray;
                }
            }
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
