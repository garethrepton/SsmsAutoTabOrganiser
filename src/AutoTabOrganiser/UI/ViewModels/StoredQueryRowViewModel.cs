using System.IO;
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
    internal sealed class StoredQueryRowViewModel
    {
        public TabSummary Source { get; }
        public GitFileStatus Status { get; }
        public string FilePath { get; }

        public StoredQueryRowViewModel(TabSummary source, GitFileStatus status, string path)
        {
            Source = source;
            Status = status;
            FilePath = path;
        }

        public string FileName => string.IsNullOrEmpty(FilePath)
            ? (Source.Name ?? "(unnamed)")
            : Path.GetFileName(FilePath);

        public string Letter
        {
            get
            {
                switch (Status)
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
                switch (Status)
                {
                    case GitFileStatus.Modified:  return "Modified";
                    case GitFileStatus.Untracked: return "Untracked";
                    case GitFileStatus.Staged:    return "Staged (added)";
                    default: return Status.ToString();
                }
            }
        }

        public Brush LetterBrush
        {
            get
            {
                switch (Status)
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
    }
}
