using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Yozolab.Tabstep
{
    partial class TabstepProjectWindow
    {
        /// <summary>Path as a single GenericMenu item label — '/' would create submenus.</summary>
        static string MenuPath(string path)
        {
            return (path ?? "").Replace("/", " › ");
        }

        // ---- path bar copy / paste ----------------------------------------------

        /// <summary>Project root on disk — the folder that contains Assets.</summary>
        static string ProjectRoot => ProjectPaths.GetParent(ProjectPaths.Normalize(Application.dataPath));

        /// <summary>
        /// Resolves clipboard/typed text to a folder to show. Text naming an asset file
        /// resolves to its parent folder, with the asset itself returned for pinging.
        /// Null when the text doesn't point inside the project.
        /// </summary>
        static string ResolveExternalPath(string input, out string pingAssetPath)
        {
            pingAssetPath = null;
            var path = ProjectPaths.ToProjectPath(input, ProjectRoot);
            if (path == null) return null;
            if (AssetDatabase.IsValidFolder(path)) return path;
            if (AssetDatabase.GetMainAssetTypeAtPath(path) == null) return null;
            pingAssetPath = path;
            return ProjectPaths.GetParent(path);
        }

        static string ToAbsolutePath(string projectPath)
        {
            try
            {
                // GetPhysicalPath resolves Packages/... into the real package location.
                return Path.GetFullPath(FileUtil.GetPhysicalPath(projectPath));
            }
            catch
            {
                return projectPath;
            }
        }

        /// <summary>Pings after the next sync so the embedded browser already shows the folder.</summary>
        void PingLater(string assetPath)
        {
            if (assetPath == null) return;
            EditorApplication.delayCall += () =>
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null) return;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            };
        }

        void PastePathIntoActiveTab()
        {
            var folder = ResolveExternalPath(EditorGUIUtility.systemCopyBuffer, out var pingPath);
            if (folder == null)
            {
                ShowNotification(new GUIContent("Clipboard has no project path"));
                return;
            }
            NavigateTo(folder);
            PingLater(pingPath);
        }

        void BeginPathEdit()
        {
            if (!TabstepSettings.ShowNavigationBar || _session.ActiveTab == null) return;
            _editingPath = true;
            _focusPathField = true;
            _pathEditText = _session.ActiveTab.CurrentPath ?? "";
            Repaint();
        }

        void CancelPathEdit()
        {
            _editingPath = false;
            GUIUtility.keyboardControl = 0;
            ClearPathSuggestions();
            Repaint();
        }

        void CommitPathEdit()
        {
            _editingPath = false;
            GUIUtility.keyboardControl = 0;
            ClearPathSuggestions();
            var folder = ResolveExternalPath(_pathEditText, out var pingPath);
            if (folder == null)
            {
                ShowNotification(new GUIContent("Folder not found:\n" + _pathEditText.Trim()));
                return;
            }
            NavigateTo(folder);
            PingLater(pingPath);
        }
    }
}
