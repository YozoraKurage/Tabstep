using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    // Status bar: the bottom row showing the shown folder's item count and a summary of
    // the selected assets. Both are cached on a timer (listing folders / sizing files
    // every repaint is not free).
    partial class TabstepProjectWindow
    {
        string _statusFolder;
        int _statusItemCount;
        double _statusFolderTime;
        string _statusSelectionText = "";
        double _statusSelectionTime;

        static GUIStyle _rightMiniLabel;
        static GUIStyle RightMiniLabel => _rightMiniLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };

        /// <summary>Bottom row: item count of the shown folder, and the selection summary.</summary>
        void DrawStatusBar(Rect rect)
        {
            if (Event.current.type == EventType.Repaint)
                EditorStyles.toolbar.Draw(rect, GUIContent.none, false, false, false, false);
            var tab = _session.ActiveTab;
            string left = tab?.CurrentPath == null
                ? "New Tab"
                : $"{FolderItemCount(tab.CurrentPath)} items";
            GUI.Label(new Rect(rect.x + 6, rect.y, rect.width / 2, rect.height),
                left, EditorStyles.miniLabel);
            var right = SelectionSummary();
            if (right.Length > 0)
                GUI.Label(new Rect(rect.x + rect.width / 2, rect.y, rect.width / 2 - 6, rect.height),
                    right, RightMiniLabel);
        }

        /// <summary>Direct (non-recursive) children, .meta files excluded; cached for 2 seconds.</summary>
        int FolderItemCount(string folder)
        {
            if (folder == _statusFolder &&
                EditorApplication.timeSinceStartup - _statusFolderTime < 2)
                return _statusItemCount;
            _statusFolder = folder;
            _statusFolderTime = EditorApplication.timeSinceStartup;
            _statusItemCount = 0;
            try
            {
                var physical = Path.GetFullPath(FileUtil.GetPhysicalPath(folder));
                if (Directory.Exists(physical))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(physical))
                        if (!entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                            _statusItemCount++;
            }
            catch
            {
                // Inaccessible folder (immutable package on a network drive...) — show 0.
            }
            return _statusItemCount;
        }

        /// <summary>"3 selected • 1.2 MB" for the selected assets; size capped at 100 files.</summary>
        string SelectionSummary()
        {
            if (EditorApplication.timeSinceStartup - _statusSelectionTime < 0.5)
                return _statusSelectionText;
            _statusSelectionTime = EditorApplication.timeSinceStartup;
            var guids = Selection.assetGUIDs;
            if (guids.Length == 0) return _statusSelectionText = "";
            long bytes = 0;
            int files = 0;
            int limit = Math.Min(guids.Length, 100);
            for (int i = 0; i < limit; i++)
            {
                try
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    var physical = Path.GetFullPath(FileUtil.GetPhysicalPath(path));
                    if (File.Exists(physical))
                    {
                        bytes += new FileInfo(physical).Length;
                        files++;
                    }
                }
                catch
                {
                    // Skip whatever cannot be sized.
                }
            }
            var text = guids.Length + " selected";
            if (files > 0)
                text += "  •  " + EditorUtility.FormatBytes(bytes) + (guids.Length > limit ? "+" : "");
            return _statusSelectionText = text;
        }
    }
}
