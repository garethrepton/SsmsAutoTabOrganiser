using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>
    /// One pinned-tag section in the side panel: header (#tag), unpin button, and a list of
    /// tabs filtered to that tag.
    /// </summary>
    internal sealed class PinnedSectionViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Tag { get; }
        public string Header => "#" + Tag;
        public ObservableCollection<TabRowViewModel> Items { get; } = new ObservableCollection<TabRowViewModel>();
        public ICommand UnpinCommand { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; Notify(); }
        }

        public PinnedSectionViewModel(string tag, ICommand unpinCommand)
        {
            Tag = tag;
            UnpinCommand = unpinCommand;
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
