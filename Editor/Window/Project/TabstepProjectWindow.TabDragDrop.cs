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
        // ---- tab drag reorder ----------------------------------------------------

        /// <summary>Drag a tab header sideways to reorder; pinned tabs stay among pinned.</summary>
        void HandleTabReorder(int controlId)
        {
            if (GUIUtility.hotControl != controlId) return;
            var e = Event.current;
            switch (e.rawType)
            {
                case EventType.MouseDrag:
                    if (_reorderIndex < 0 || _reorderIndex >= _session.Count) break;
                    if (!_reordering && Mathf.Abs(e.mousePosition.x - _reorderStartX) < 5f)
                    {
                        e.Use();
                        break;
                    }
                    _reordering = true;
                    int target = TabIndexAt(e.mousePosition.x);
                    if (target >= 0 && target != _reorderIndex && CrossedCenter(target, e.mousePosition.x))
                    {
                        int moved = _session.MoveTab(_reorderIndex, target);
                        if (moved >= 0) _reorderIndex = moved;
                        Repaint();
                    }
                    e.Use();
                    break;
                case EventType.MouseUp:
                    GUIUtility.hotControl = 0;
                    _reorderIndex = -1;
                    _reordering = false;
                    e.Use();
                    break;
            }
        }

        int TabIndexAt(float x)
        {
            int count = Math.Min(_session.Count, _tabRects.Count);
            for (int i = 0; i < count; i++)
                if (x >= _tabRects[i].xMin && x < _tabRects[i].xMax)
                    return i;
            return -1;
        }

        /// <summary>Swap only once the cursor passes the neighbour's center — avoids flicker.</summary>
        bool CrossedCenter(int target, float x)
        {
            if (target < 0 || target >= _tabRects.Count) return false;
            float center = _tabRects[target].center.x;
            return target > _reorderIndex ? x > center : x < center;
        }

        void ShowTabContextMenu(int index)
        {
            var menu = new GenericMenu();
            // Top row: the OS file browser on this tab's own folder. The stock Assets
            // menu's reveal acts on the current selection, which is not what a click on
            // a tab means — and the tab clicked need not be the active one.
            var tabPath = _session.Tabs[index].CurrentPath;
            if (FileBrowser.FolderExists(tabPath))
                menu.AddItem(new GUIContent(FileBrowser.OpenFolderLabel), false, () => OpenFolderOrNotify(tabPath));
            else
                menu.AddDisabledItem(new GUIContent(FileBrowser.OpenFolderLabel));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Close Tab"), false, () => CloseTab(index));
            if (_session.Count > 1)
                menu.AddItem(new GUIContent("Close Other Tabs"), false, () =>
                {
                    _session.CloseOthers(index);
                    _applyTabToBrowser = true;
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Close Other Tabs"));
            if (index < _session.Count - 1)
                menu.AddItem(new GUIContent("Close Tabs to the Right"), false, () =>
                {
                    _session.CloseToRight(index);
                    _applyTabToBrowser = true;
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Close Tabs to the Right"));
            if (_session.HasClosedTabs)
                menu.AddItem(new GUIContent("Reopen Closed Tab"), false, ReopenClosedTab);
            else
                menu.AddDisabledItem(new GUIContent("Reopen Closed Tab"));
            menu.AddSeparator("");
            var tab = _session.Tabs[index];
            menu.AddItem(new GUIContent(tab.Pinned ? "Unpin Tab" : "Pin Tab"), false, () =>
            {
                _session.SetPinned(index, !tab.Pinned);
                Repaint();
            });
            if (TabstepBookmarks.Contains(tab.CurrentPath))
                menu.AddItem(new GUIContent("Remove from Quick Access"), false,
                    () => TabstepBookmarks.Remove(tab.CurrentPath));
            else
                menu.AddItem(new GUIContent("Add to Quick Access"), false,
                    () => TabstepBookmarks.Add(tab.CurrentPath));
            // Captured now: includes the folder tree pane's selection, and the menu
            // interaction itself must not change what gets sent.
            var shelfSelection = TabstepShelfWindow.SelectionForShelf();
            if (shelfSelection.Length > 0)
                menu.AddItem(new GUIContent("Send Selection to Shelf"), false,
                    () => TabstepShelfWindow.ShowNear(this).AddObjects(shelfSelection));
            else
                menu.AddDisabledItem(new GUIContent("Send Selection to Shelf"));
            if (_lastMove.Count > 0)
                menu.AddItem(new GUIContent("Undo Last Asset Move"), false, UndoLastMove);
            else
                menu.AddDisabledItem(new GUIContent("Undo Last Asset Move"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Duplicate Tab"), false, () =>
            {
                _session.DuplicateTab(index);
                _applyTabToBrowser = true;
                Repaint();
            });
            menu.AddItem(new GUIContent("Open in New Window"), false, () => OpenTabInNewWindow(index));
            if (_session.Count > 1)
                menu.AddItem(new GUIContent("Separate Tab"), false, () => SeparateTab(index));
            else
                menu.AddDisabledItem(new GUIContent("Separate Tab"));
            menu.AddItem(new GUIContent("Duplicate Window"), false, DuplicateWindow);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Path"), false,
                () => EditorGUIUtility.systemCopyBuffer = _session.Tabs[index].CurrentPath);
            var pastedFolder = ResolveExternalPath(EditorGUIUtility.systemCopyBuffer, out var pingPath);
            if (pastedFolder != null)
                menu.AddItem(new GUIContent("Paste Path"), false, () =>
                {
                    _session.Tabs[index].Navigate(pastedFolder);
                    if (index == _session.ActiveIndex)
                    {
                        _applyTabToBrowser = true;
                        PingLater(pingPath);
                    }
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Paste Path"));
            menu.ShowAsContext();
        }

        /// <summary>
        /// Drag &amp; drop onto a tab header — the Explorer way to move assets between
        /// tabs. Dropping moves the dragged assets into the tab's folder; hovering for
        /// a moment spring-loads the tab (switches to it) so the drag can continue
        /// into a subfolder inside the view.
        /// </summary>
        void HandleTabDrag(Rect rect, int index, TabState tab)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!rect.Contains(e.mousePosition))
            {
                if (_dragHoverTab == index) _dragHoverTab = -1;
                return;
            }
            var paths = DraggedProjectPaths();
            if (paths.Count == 0) return; // non-asset drag: leave it to the bar handler

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            if (e.type == EventType.DragUpdated)
            {
                if (_dragHoverTab != index)
                {
                    _dragHoverTab = index;
                    _dragHoverStart = EditorApplication.timeSinceStartup;
                }
                else if (index != _session.ActiveIndex &&
                         EditorApplication.timeSinceStartup - _dragHoverStart > SpringLoadDelay)
                {
                    ActivateTab(index); // spring-load while the drag stays alive
                }
                Repaint(); // keep the spring-load timer ticking
            }
            else
            {
                DragAndDrop.AcceptDrag();
                _dragHoverTab = -1;
                MoveAssetsTo(tab.CurrentPath, paths);
            }
            e.Use();
        }

        /// <summary>
        /// Project-relative asset paths of the current drag (empty for scene-object drags).
        /// Reads both <see cref="DragAndDrop.paths"/> and <see cref="DragAndDrop.objectReferences"/>:
        /// the stock browser's tree pane only populates the latter, so reading just paths
        /// silently dropped tree-originated drags onto tab headers.
        /// </summary>
        static List<string> DraggedProjectPaths()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var paths = new List<string>();
            foreach (var raw in DragAndDrop.paths)
            {
                var path = ProjectPaths.Normalize(raw);
                if (path == null || !seen.Add(path)) continue;
                if (AssetDatabase.IsValidFolder(path) || AssetDatabase.GetMainAssetTypeAtPath(path) != null)
                    paths.Add(path);
            }
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj == null) continue;
                var path = ProjectPaths.Normalize(AssetDatabase.GetAssetPath(obj));
                if (path == null || !seen.Add(path)) continue;
                if (AssetDatabase.IsValidFolder(path) || AssetDatabase.GetMainAssetTypeAtPath(path) != null)
                    paths.Add(path);
            }
            return paths;
        }

        int MoveAssetsTo(string targetFolder, List<string> paths)
        {
            if (string.IsNullOrEmpty(targetFolder) || !AssetDatabase.IsValidFolder(targetFolder)) return 0;
            var performed = new List<(string from, string to)>();
            foreach (var path in paths)
            {
                if (path == targetFolder) continue;
                if (ProjectPaths.GetParent(path) == targetFolder) continue; // already there
                if (targetFolder.StartsWith(path + "/", StringComparison.Ordinal)) continue; // folder into its own child
                var destination = AssetDatabase.GenerateUniqueAssetPath(
                    targetFolder + "/" + ProjectPaths.GetDisplayName(path));
                var error = AssetDatabase.MoveAsset(path, destination);
                if (string.IsNullOrEmpty(error)) performed.Add((path, destination));
                else Debug.LogWarning($"[Tabstep] Could not move '{path}': {error}");
            }
            if (performed.Count > 0)
            {
                _lastMove = performed; // context menus offer "Undo Last Asset Move"
                ShowNotification(new GUIContent(
                    $"Moved {performed.Count} asset{(performed.Count == 1 ? "" : "s")} to {targetFolder}"));
                Repaint();
            }
            return performed.Count;
        }

        /// <summary>Puts the assets of the last move back where they came from.</summary>
        void UndoLastMove()
        {
            int restored = 0;
            for (int i = _lastMove.Count - 1; i >= 0; i--)
            {
                var (from, to) = _lastMove[i];
                var parent = ProjectPaths.GetParent(from);
                if (parent == null || !AssetDatabase.IsValidFolder(parent)) continue;
                var destination = AssetDatabase.GenerateUniqueAssetPath(from);
                if (string.IsNullOrEmpty(AssetDatabase.MoveAsset(to, destination))) restored++;
            }
            _lastMove.Clear();
            if (restored > 0)
                ShowNotification(new GUIContent(
                    $"Moved {restored} asset{(restored == 1 ? "" : "s")} back"));
        }

        /// <summary>Dropping folders onto the tab bar opens each of them as a new tab.</summary>
        void HandleTabBarDragAndDrop(Rect barRect)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!barRect.Contains(e.mousePosition)) return;

            var folders = new List<string>();
            foreach (var obj in DragAndDrop.objectReferences)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (AssetDatabase.IsValidFolder(path)) folders.Add(path);
            }
            if (folders.Count == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var folder in folders)
                    OpenInNewTab(folder);
            }
            e.Use();
        }
    }
}
