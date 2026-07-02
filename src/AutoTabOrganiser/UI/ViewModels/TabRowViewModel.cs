using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutoTabOrganiser.Editor;
using AutoTabOrganiser.Git;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;

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

        /// <summary>
        /// Second line of the row: folder, then the connection the tab last ran against.
        /// Connection-aware rows make `server:PRD01`-style filtering legible — you can see
        /// at a glance which environment a hit belongs to.
        /// </summary>
        public string Subtitle
        {
            get
            {
                var parts = new List<string>(2);
                if (!string.IsNullOrEmpty(Source.Folder)) parts.Add(Source.Folder + "/");
                var conn = ConnectionShort;
                if (!string.IsNullOrEmpty(conn)) parts.Add(conn);
                return string.Join("  ·  ", parts);
            }
        }

        private string ConnectionShort
        {
            get
            {
                var s = (Source.Server ?? "").Trim();
                var d = (Source.Database ?? "").Trim();
                if (s.Length == 0 && d.Length == 0) return "";
                if (s.Length == 0) return d;
                if (d.Length == 0) return s;
                return s + "·" + d;
            }
        }

        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public bool HasTags => Tags != null && Tags.Count > 0;

        public string TooltipText
        {
            get
            {
                var name = Source.Name ?? "(unnamed)";
                var folder = string.IsNullOrEmpty(Source.Folder) ? "" : "\nFolder: " + Source.Folder;
                var server = string.IsNullOrEmpty(Source.Server) ? "" : "\nServer: " + Source.Server;
                var db = string.IsNullOrEmpty(Source.Database) ? "" : "\nDatabase: " + Source.Database;
                var id = string.IsNullOrEmpty(Source.TabId) ? "" : "\nId: " + Source.TabId;
                var tags = string.IsNullOrEmpty(Source.TagsCsv) ? "" : "\nTags: " + Source.TagsCsv;
                return name + folder + server + db + tags + id;
            }
        }

        public bool IsOpen => Source.IsOpen;
        public bool IsDirty => Source.IsDirty;

        /// <summary>Bare search terms to bold in the title. Quick Switcher only; null elsewhere.</summary>
        public IList<string> HighlightTerms { get; set; }

        /// <summary>1-based Ctrl+N jump hint. Quick Switcher sets it on the first nine rows.</summary>
        public string ShortcutHint { get; set; }
        public bool HasShortcutHint => !string.IsNullOrEmpty(ShortcutHint);
        public string ShortcutTooltip => HasShortcutHint ? "Ctrl+" + ShortcutHint : null;

        /// <summary>Compact "5m" — how recently the user focused or edited the tab.</summary>
        public string ActivityTimeText => RelativeTime.Format(Source.EffectiveActivatedTs);

        /// <summary>Compact "edited Xm" — driven by the latest snapshot of any reason.</summary>
        public string LastEditedText => "edited " + RelativeTime.Format(Source.Ts);

        /// <summary>Compact "saved Xd" — null until the user explicitly Saves to scripts.</summary>
        public string LastSavedText => Source.LastSavedTs.HasValue
            ? "saved " + RelativeTime.Format(Source.LastSavedTs.Value)
            : null;

        public bool HasLastSaved => Source.LastSavedTs.HasValue;

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
