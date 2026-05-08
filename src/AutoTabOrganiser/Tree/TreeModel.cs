using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.Tree
{
    internal sealed class TabTreeNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; set; }
        public string FullPath { get; set; }
        public ObservableCollection<TabTreeNode> Folders { get; } = new ObservableCollection<TabTreeNode>();
        public ObservableCollection<TabSummary> Tabs { get; } = new ObservableCollection<TabSummary>();
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
        }
        private bool _isExpanded = true;

        public bool IsFolder => true;
        public override string ToString() => $"{Name} ({Folders.Count} folders, {Tabs.Count} tabs)";
    }

    internal static class TreeBuilder
    {
        public static TabTreeNode BuildTree(IEnumerable<TabSummary> tabs, string sortMode)
        {
            var root = new TabTreeNode { Name = "(root)", FullPath = "" };
            var folders = new Dictionary<string, TabTreeNode>(StringComparer.OrdinalIgnoreCase);
            folders[""] = root;

            foreach (var t in tabs)
            {
                var folderPath = string.IsNullOrWhiteSpace(t.Folder) ? "Unfiled" : t.Folder;
                var node = EnsureFolder(folders, folderPath, root);
                node.Tabs.Add(t);
            }

            SortRecursive(root, sortMode);
            return root;
        }

        private static TabTreeNode EnsureFolder(Dictionary<string, TabTreeNode> map, string fullPath, TabTreeNode root)
        {
            if (map.TryGetValue(fullPath, out var n)) return n;
            var parts = fullPath.Split('/');
            var current = root;
            var soFar = "";
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                soFar = string.IsNullOrEmpty(soFar) ? part : soFar + "/" + part;
                if (!map.TryGetValue(soFar, out var child))
                {
                    child = new TabTreeNode { Name = part, FullPath = soFar };
                    current.Folders.Add(child);
                    map[soFar] = child;
                }
                current = child;
            }
            return current;
        }

        private static void SortRecursive(TabTreeNode node, string sortMode)
        {
            var sortedFolders = node.Folders.OrderBy(f => f.Name == "Unfiled" ? 1 : 0)
                                            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            node.Folders.Clear();
            foreach (var f in sortedFolders) { node.Folders.Add(f); SortRecursive(f, sortMode); }

            IEnumerable<TabSummary> ordered = node.Tabs;
            switch (sortMode)
            {
                case "name-asc":  ordered = ordered.OrderBy(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase); break;
                case "name-desc": ordered = ordered.OrderByDescending(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase); break;
                case "folder-name": ordered = ordered.OrderBy(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase); break;
                case "recent":
                default: ordered = ordered.OrderByDescending(t => t.Ts); break;
            }
            var list = ordered.ToList();
            node.Tabs.Clear();
            foreach (var t in list) node.Tabs.Add(t);
        }
    }
}
