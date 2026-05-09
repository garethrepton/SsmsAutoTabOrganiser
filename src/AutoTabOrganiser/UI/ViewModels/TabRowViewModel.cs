using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutoTabOrganiser.Editor;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.UI.ViewModels
{
    /// <summary>
    /// One row in the Tabs/Recent/Pinned lists. Wraps a <see cref="TabSummary"/> and adds
    /// derived display fields plus a mutable git status (resolved off-thread, then pushed
    /// back to the UI via property change notification).
    /// </summary>
    internal sealed class TabRowViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public TabSummary Source { get; }
        public IList<TagChip> Tags { get; }

        private GitFileStatus _gitStatus;

        public TabRowViewModel(TabSummary s, IDictionary<string, string> tagColours)
        {
            Source = s;
            _gitStatus = GitFileStatus.Unknown;
            Tags = BuildChips(s.TagsCsv, tagColours);
        }

        public GitFileStatus GitStatus
        {
            get => _gitStatus;
            set
            {
                if (_gitStatus == value) return;
                _gitStatus = value;
                Notify(nameof(GitStatus));
                Notify(nameof(GitMonikerName));
                Notify(nameof(GitTooltip));
            }
        }

        public string Title => Source.Name ?? "(unnamed)";

        public string Subtitle =>
            string.IsNullOrEmpty(Source.Folder) ? "" : Source.Folder + "/";

        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public bool HasTags => Tags != null && Tags.Count > 0;

        public string TooltipText
        {
            get
            {
                var name = Source.Name ?? "(unnamed)";
                var folder = string.IsNullOrEmpty(Source.Folder) ? "" : "\nFolder: " + Source.Folder;
                var id = string.IsNullOrEmpty(Source.TabId) ? "" : "\nId: " + Source.TabId;
                var tags = string.IsNullOrEmpty(Source.TagsCsv) ? "" : "\nTags: " + Source.TagsCsv;
                return name + folder + tags + id;
            }
        }

        public bool IsOpen => Source.IsOpen;
        public bool IsDirty => Source.IsDirty;

        public string GitMonikerName
        {
            get
            {
                switch (_gitStatus)
                {
                    case GitFileStatus.Modified:  return "PendingChanges";
                    case GitFileStatus.Staged:    return "PendingAddNode";
                    case GitFileStatus.Untracked: return "DocumentOutline";
                    case GitFileStatus.Clean:     return "StatusOK";
                    default:                      return null;
                }
            }
        }

        public string GitTooltip
        {
            get
            {
                switch (_gitStatus)
                {
                    case GitFileStatus.Modified:  return "Modified";
                    case GitFileStatus.Staged:    return "Staged";
                    case GitFileStatus.Untracked: return "Untracked";
                    case GitFileStatus.Clean:     return "Clean";
                    case GitFileStatus.NotInRepo: return "Not in a git repo";
                    default:                      return "";
                }
            }
        }

        public override string ToString() => Title;

        private static IList<TagChip> BuildChips(string csv, IDictionary<string, string> overrides)
        {
            var list = new List<TagChip>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (var raw in csv.Split(','))
            {
                var t = (raw ?? "").Trim();
                if (t.Length == 0) continue;
                list.Add(new TagChip(t, TagColourResolver.Resolve(t, overrides)));
            }
            return list;
        }

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
