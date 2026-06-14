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
        // ---- navigation actions ----------------------------------------------

        void ActivateTab(int index)
        {
            _session.Activate(index);
            _applyTabToBrowser = true;
            Repaint();
        }

        void CloseTab(int index)
        {
            if (!_session.CloseTab(index)) return;
            if (_session.Count == 0)
            {
                Close();
                return;
            }
            _applyTabToBrowser = true;
            Repaint();
        }

        void NavigateTo(string folderPath)
        {
            var tab = _session.ActiveTab;
            if (tab == null || !AssetDatabase.IsValidFolder(folderPath)) return;
            tab.Navigate(folderPath);
            _applyTabToBrowser = true;
            Repaint();
        }

        void GoBack()
        {
            _session.ActiveTab?.GoBack();
            _applyTabToBrowser = true;
            Repaint();
        }

        void GoForward()
        {
            _session.ActiveTab?.GoForward();
            _applyTabToBrowser = true;
            Repaint();
        }

        void GoUp()
        {
            var parent = ProjectPaths.GetParent(_session.ActiveTab?.CurrentPath);
            if (parent != null) NavigateTo(parent);
        }

        void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;
            bool ctrl = e.control || e.command;

            if (ctrl && e.shift && e.keyCode == KeyCode.T)
            {
                ReopenClosedTab();
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.T)
            {
                OpenInNewTab(null);
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.W)
            {
                // Pinned tabs don't close from the keyboard — that's the point of the pin.
                if (_session.ActiveTab != null && _session.ActiveTab.Pinned)
                    ShowNotification(new GUIContent("Tab is pinned"));
                else
                    CloseTab(_session.ActiveIndex);
                e.Use();
            }
            else if (ctrl && e.shift && e.keyCode == KeyCode.C)
            {
                if (_session.ActiveTab?.CurrentPath != null)
                {
                    EditorGUIUtility.systemCopyBuffer = ToAbsolutePath(_session.ActiveTab.CurrentPath);
                    ShowNotification(new GUIContent("Absolute path copied"));
                }
                e.Use();
            }
            else if (ctrl && e.shift && e.keyCode == KeyCode.D)
            {
                // Fallback for the global "Tabstep/Summon Shelf" shortcut — covers
                // setups where the Shortcut Manager binding is shadowed. When the
                // global binding fires first, this window never sees the key.
                TabstepShelfWindow.SummonToMouse();
                e.Use();
            }
            else if (ctrl && !e.shift && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9)
            {
                // Ctrl+1..8 jump to that tab, Ctrl+9 to the last one (browser convention).
                int target = e.keyCode == KeyCode.Alpha9
                    ? _session.Count - 1
                    : e.keyCode - KeyCode.Alpha1;
                if (target >= 0 && target < _session.Count) ActivateTab(target);
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.Tab)
            {
                _session.CycleActive(e.shift ? -1 : 1);
                _applyTabToBrowser = true;
                e.Use();
            }
            else if (e.alt && e.keyCode == KeyCode.LeftArrow)
            {
                GoBack();
                e.Use();
            }
            else if (e.alt && e.keyCode == KeyCode.RightArrow)
            {
                GoForward();
                e.Use();
            }
            else if (e.alt && e.keyCode == KeyCode.UpArrow)
            {
                GoUp();
                e.Use();
            }
            else if ((ctrl && e.keyCode == KeyCode.L) || (e.alt && e.keyCode == KeyCode.D))
            {
                BeginPathEdit();
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.F &&
                     TabstepSettings.ShowNavigationBar && ProjectBrowserPatcher.Active)
            {
                // Only when the search field lives in our bar; otherwise the browser's
                // own toolbar handles Ctrl+F itself.
                (_searchField ??= new SearchField()).SetFocus();
                e.Use();
                Repaint();
            }
            else if (CanHandleWasdKey(e))
            {
                HandleWasdKey(e);
            }
        }

        // ---- WASD selection navigation ------------------------------------------

        /// <summary>
        /// Bare W/A/S/D only, and never while the user is typing — renaming an asset,
        /// the search field and the path bar all put IMGUI into text editing mode.
        /// </summary>
        bool CanHandleWasdKey(Event e)
        {
            if (!TabstepSettings.WasdSelectionNavigation) return false;
            if (e.control || e.command || e.alt || e.shift) return false;
            if (_editingPath || EditorGUIUtility.editingTextField) return false;
            return e.keyCode == KeyCode.W || e.keyCode == KeyCode.A ||
                   e.keyCode == KeyCode.S || e.keyCode == KeyCode.D;
        }

        /// <summary>
        /// Explorer-on-the-home-row: W/S step the selection through the shown folder's
        /// items, D opens the selected folder (or asset), A goes back through the
        /// history — so D into a folder and A out of it are symmetric.
        /// </summary>
        void HandleWasdKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.W:
                    StepSelection(-1);
                    break;
                case KeyCode.S:
                    StepSelection(+1);
                    break;
                case KeyCode.A:
                    GoBack();
                    break;
                case KeyCode.D:
                    OpenSelection();
                    break;
            }
            e.Use();
        }

        /// <summary>Moves the selection to the previous/next item of the shown folder.</summary>
        void StepSelection(int delta)
        {
            var folder = _session.ActiveTab?.CurrentPath;
            // Search results follow the browser's own relevance order, which this
            // folder-based stepping cannot reproduce — leave the keys inert there.
            if (folder == null || _host.IsSearching()) return;
            var items = FolderNavigation.FolderItems(folder);
            var current = ProjectPaths.Normalize(AssetDatabase.GetAssetPath(Selection.activeObject));
            var next = FolderNavigation.NextSelectionPath(items, current, delta);
            if (next == null) return;
            var asset = AssetDatabase.LoadMainAssetAtPath(next);
            if (asset == null) return;
            Selection.activeObject = asset;
            _host.FrameObject(asset.GetInstanceID());
            Repaint();
        }

        /// <summary>
        /// Opens the selection: a folder navigates the active tab into it (recorded in
        /// the history, so A backs out again), anything else opens like a double-click.
        /// </summary>
        void OpenSelection()
        {
            var obj = Selection.activeObject;
            if (obj == null) return;
            var path = ProjectPaths.Normalize(AssetDatabase.GetAssetPath(obj));
            if (path != null && AssetDatabase.IsValidFolder(path))
                NavigateTo(path);
            else
                AssetDatabase.OpenAsset(obj);
        }

        /// <summary>
        /// Mouse side (thumb) buttons go back/forward in the active tab's history, like a
        /// web browser. Runs before the embedded browser so presses anywhere in the window
        /// count; buttons 3/4 are XButton1/XButton2 in IMGUI events.
        /// </summary>
        void HandleMouseNavigation()
        {
            if (!TabstepSettings.MouseSideButtonsNavigate) return;
            var e = Event.current;
            if (e.type != EventType.MouseDown) return;
            if (e.button == 3)
            {
                GoBack();
                e.Use();
            }
            else if (e.button == 4)
            {
                GoForward();
                e.Use();
            }
        }

        /// <summary>
        /// Tracks whether an asset drag is hovering the window — the tab bar shows the
        /// shelf drop zone only while one is. Runs before the bar and the embedded
        /// browser, so it sees the events even when they get consumed later.
        /// </summary>
        void TrackDragState()
        {
            var e = Event.current;
            if (e.type == EventType.Layout)
            {
                _dragZoneVisible = _dragActive;
                return;
            }
            if (e.type == EventType.DragUpdated)
            {
                bool active = DragAndDrop.objectReferences.Length > 0;
                if (active != _dragActive)
                {
                    _dragActive = active;
                    Repaint();
                }
            }
            // Not on DragPerform: the drop zone itself still has to see that event later
            // in this same pass. The DragExited that follows (or the first plain mouse
            // event after the drag) hides the zone again.
            else if (_dragActive &&
                     (e.type == EventType.DragExited || e.type == EventType.MouseMove ||
                      e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                _dragActive = false;
                Repaint();
            }
        }

        void ReopenClosedTab()
        {
            if (_session.ReopenClosedTab() == null)
            {
                ShowNotification(new GUIContent("No recently closed tabs"));
                return;
            }
            _applyTabToBrowser = true;
            Repaint();
        }
    }
}
