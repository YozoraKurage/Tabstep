using System;
using System.Collections.Generic;
using UnityEditor;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Keyboard folder-browsing helpers: the items a folder shows (in the browser's
    /// display order) and stepping a selection through them. Pure / static — unit-tested.
    /// </summary>
    internal static class FolderNavigation
    {
        /// <summary>
        /// Path to select when stepping by <paramref name="delta"/>, clamped at both
        /// ends. A selection outside the list (or none) starts from the nearest end:
        /// S picks the first item, W the last. Null only for an empty list.
        /// </summary>
        internal static string NextSelectionPath(List<string> items, string current, int delta)
        {
            if (items.Count == 0) return null;
            int index = current == null ? -1 : items.IndexOf(current);
            if (index < 0) return delta < 0 ? items[items.Count - 1] : items[0];
            index = Math.Max(0, Math.Min(items.Count - 1, index + delta));
            return items[index];
        }

        /// <summary>
        /// Direct children of the folder in the browser's display order: subfolders
        /// first, then assets, each naturally sorted.
        /// </summary>
        internal static List<string> FolderItems(string folder)
        {
            var items = new List<string>();
            foreach (var sub in AssetDatabase.GetSubFolders(folder))
            {
                var path = ProjectPaths.Normalize(sub);
                if (path != null) items.Add(path);
            }
            items.Sort(EditorUtility.NaturalCompare);

            var assets = new List<string>();
            var seen = new HashSet<string>();
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                var path = ProjectPaths.Normalize(AssetDatabase.GUIDToAssetPath(guid));
                // FindAssets is recursive and lists folders too — keep direct files only.
                if (path == null || !seen.Add(path)) continue;
                if (ProjectPaths.GetParent(path) != folder) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                assets.Add(path);
            }
            assets.Sort(EditorUtility.NaturalCompare);

            items.AddRange(assets);
            return items;
        }
    }
}
