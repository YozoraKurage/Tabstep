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
        // ---- tab bar -----------------------------------------------------------

        void DrawTabBar()
        {
            BuildTabTitles();
            int reorderControl = GUIUtility.GetControlID(TabReorderHash, FocusType.Passive);
            float h = EditorStyles.toolbar.fixedHeight;

            // Reserve a full-width toolbar row (with its background) to draw the tabs over.
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            var row = GUILayoutUtility.GetLastRect();

            // Controls pinned to the right edge: settings, the all-tabs / workspace list, and
            // — while a drag is in flight — the shelf drop zone.
            float right = row.xMax;
            var settingsRect = new Rect(right - 26, row.y, 26, h); right -= 26;
            var listRect = new Rect(right - 22, row.y, 22, h); right -= 22;
            Rect shelfRect = Rect.zero;
            if (_dragZoneVisible) { shelfRect = new Rect(right - 66, row.y, 66, h); right -= 66; }

            // The tabs live in the remaining strip; the mouse wheel scrolls them when they
            // overflow it. _tabRects stay in window coordinates so reorder / drag-and-drop work
            // unchanged; the right controls (drawn last) mask the right overflow and the window
            // clips the left, so no GUI clip is needed.
            var viewport = new Rect(row.x, row.y, Mathf.Max(0f, right - row.x), h);

            // Equal-width tabs share the strip like a browser: unpinned tabs take an equal
            // slice of the space left by the pinned tabs and the "+" button, clamped to a
            // sensible range; past the minimum the bar overflows and scrolls. 0 = size to title.
            float equalWidth = 0f;
            if (TabstepSettings.EqualWidthTabs)
            {
                int normal = _session.Count - _session.PinnedCount;
                if (normal > 0)
                {
                    float avail = viewport.width - 26f - _session.PinnedCount * 28f;
                    equalWidth = Mathf.Clamp(avail / normal, 60f, 200f);
                }
            }

            float contentWidth = 26f; // the "+" button
            for (int i = 0; i < _session.Count; i++) contentWidth += TabWidth(i, equalWidth);
            float maxScroll = Mathf.Max(0f, contentWidth - viewport.width);
            _tabScroll = maxScroll > 0f ? Mathf.Clamp(_tabScroll, 0f, maxScroll) : 0f;

            float x = viewport.x - _tabScroll;
            for (int i = 0; i < _session.Count; i++)
            {
                float w = TabWidth(i, equalWidth);
                var tabRect = new Rect(x, row.y, w, h);
                if (i < _tabRects.Count) _tabRects[i] = tabRect;
                if (DrawTab(i, tabRect, reorderControl, viewport))
                {
                    // Structure changed mid-loop (a tab closed) — bail out of this pass.
                    GUIUtility.ExitGUI();
                    return;
                }
                x += w;
            }
            DrawNewTabButton(new Rect(x, row.y, 26f, h), viewport);

            if (_dragZoneVisible) DrawShelfDropZone(shelfRect);
            DrawTabListButton(listRect);
            DrawSettingsButton(settingsRect);

            HandleTabBarScroll(viewport, maxScroll);
            HandleTabReorder(reorderControl);
            HandleTabBarDragAndDrop(row);
        }

        /// <summary>
        /// The visible width a tab occupies in the bar. <paramref name="equalWidth"/> &gt; 0
        /// forces that uniform width on unpinned tabs; 0 sizes each tab to its title.
        /// </summary>
        float TabWidth(int index, float equalWidth)
        {
            if (_session.Tabs[index].Pinned) return 28f;
            if (equalWidth > 0f) return equalWidth;
            var content = TabContent(index, index == _session.ActiveIndex);
            return Mathf.Min(EditorStyles.toolbarButton.CalcSize(content).x, 200f);
        }

        /// <summary>The label / icon a tab draws (active tabs reserve room for the close glyph).</summary>
        GUIContent TabContent(int index, bool active)
        {
            var tab = _session.Tabs[index];
            if (tab.Pinned)
            {
                var icon = tab.CurrentPath != null ? AssetDatabase.GetCachedIcon(tab.CurrentPath) : null;
                return icon != null
                    ? new GUIContent(icon, tab.CurrentPath)
                    : new GUIContent(ProjectPaths.Ellipsize(_tabTitles[index], 4), tab.CurrentPath);
            }
            string title = _tabTitles[index];
            // Trailing spaces reserve room for the close glyph drawn over the active tab.
            return new GUIContent(active ? title + "    " : title, tab.CurrentPath);
        }

        /// <summary>The Tabstep preferences button, at the right end of the tab bar.</summary>
        void DrawSettingsButton(Rect rect)
        {
            var content = new GUIContent(EditorGUIUtility.IconContent("_Popup").image,
                "Tabstep preferences");
            if (GUI.Button(rect, content, EditorStyles.toolbarButton))
                SettingsService.OpenUserPreferences(TabstepSettingsProvider.Path);
        }

        /// <summary>Mouse wheel over the tab strip scrolls it horizontally when tabs overflow.</summary>
        void HandleTabBarScroll(Rect viewport, float maxScroll)
        {
            var e = Event.current;
            if (e.type != EventType.ScrollWheel || maxScroll <= 0f) return;
            if (!viewport.Contains(e.mousePosition)) return;
            _tabScroll = Mathf.Clamp(_tabScroll + e.delta.y * 20f, 0f, maxScroll);
            e.Use();
            Repaint();
        }

        /// <summary>
        /// Tab titles for this pass. Tabs sharing a display name (Scripts, Textures...)
        /// get their parent folder appended so they stay distinguishable.
        /// </summary>
        void BuildTabTitles()
        {
            _tabTitles.Clear();
            var counts = new Dictionary<string, int>();
            foreach (var tab in _session.Tabs)
            {
                var name = ProjectPaths.GetDisplayName(tab.CurrentPath) ?? "New Tab";
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
            }
            foreach (var tab in _session.Tabs)
            {
                var name = ProjectPaths.GetDisplayName(tab.CurrentPath) ?? "New Tab";
                if (counts[name] > 1)
                {
                    var parent = ProjectPaths.GetDisplayName(ProjectPaths.GetParent(tab.CurrentPath));
                    if (parent != null) name = name + " — " + parent;
                }
                _tabTitles.Add(ProjectPaths.Ellipsize(name, TabstepSettings.MaxTabTitleLength));
            }
            while (_tabRects.Count < _session.Count) _tabRects.Add(Rect.zero);
            while (_tabRects.Count > _session.Count) _tabRects.RemoveAt(_tabRects.Count - 1);
        }

        /// <summary>Draws one tab. Returns true when the tab was closed (layout is now stale).</summary>
        bool DrawTab(int index, Rect rect, int reorderControl, Rect viewport)
        {
            var tab = _session.Tabs[index];
            bool active = index == _session.ActiveIndex;
            var style = EditorStyles.toolbarButton;
            var content = TabContent(index, active);
            var closeRect = new Rect(rect.xMax - 18, rect.y + (rect.height - 16) / 2, 16, 16);

            var e = Event.current;
            // Gate input to the visible strip: a tab scrolled under the right controls must
            // not steal their clicks, and the window already clips anything off the left.
            bool inViewport = viewport.Contains(e.mousePosition);

            if (inViewport) HandleTabDrag(rect, index, tab);

            if (inViewport && e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                if (e.button == 0)
                {
                    if (active && !tab.Pinned && closeRect.Contains(e.mousePosition))
                    {
                        e.Use();
                        CloseTab(index);
                        return true;
                    }
                    // Activate on press (not release) so a reorder drag starts from the
                    // same press; the window-level control keeps the capture while the
                    // tab indices shift under the cursor.
                    if (!active) ActivateTab(index);
                    GUIUtility.hotControl = reorderControl;
                    _reorderIndex = index;
                    _reorderStartX = e.mousePosition.x;
                    _reordering = false;
                    e.Use();
                    return false;
                }
                if (e.button == 2 && TabstepSettings.MiddleClickClosesTab && !tab.Pinned)
                {
                    e.Use();
                    CloseTab(index);
                    return true;
                }
                if (e.button == 1)
                {
                    e.Use();
                    ShowTabContextMenu(index);
                    return false;
                }
            }

            // Render only (no interactive control): an overflowing tab sitting under the
            // right controls must not swallow their clicks. Input is handled manually above.
            if (e.type == EventType.Repaint)
                style.Draw(rect, content, inViewport && rect.Contains(e.mousePosition), false, active, false);
            if (active && !tab.Pinned)
                GUI.Label(closeRect, new GUIContent("×", "Close tab (Ctrl+W)"), EditorStyles.miniLabel);
            return false;
        }

        void DrawNewTabButton(Rect rect, Rect viewport)
        {
            var content = new GUIContent("+", "New tab (Ctrl+T)\nRight-click: Quick Access");
            var e = Event.current;
            bool inViewport = viewport.Contains(e.mousePosition);
            if (inViewport && e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                if (e.button == 1) { e.Use(); ShowQuickAccessMenu(rect); return; }
                if (e.button == 0) { e.Use(); OpenInNewTab(null); return; }
            }
            if (e.type == EventType.Repaint)
                EditorStyles.toolbarButton.Draw(rect, content,
                    inViewport && rect.Contains(e.mousePosition), false, false, false);
        }

        /// <summary>
        /// Quick Access — bookmarked folders and saved searches that open as new tabs
        /// (right-click the +).
        /// </summary>
        void ShowQuickAccessMenu(Rect dropRect)
        {
            var menu = new GenericMenu();
            if (TabstepBookmarks.Folders.Count == 0 && TabstepBookmarks.Searches.Count == 0)
                menu.AddDisabledItem(new GUIContent("Quick Access is empty"));
            foreach (var folder in TabstepBookmarks.Folders)
            {
                var path = folder;
                if (AssetDatabase.IsValidFolder(path))
                    menu.AddItem(new GUIContent(MenuPath(path)), false, () => OpenInNewTab(path));
                else
                    menu.AddDisabledItem(new GUIContent(MenuPath(path)));
            }
            foreach (var saved in TabstepBookmarks.Searches)
            {
                var entry = saved;
                var label = SavedSearchLabel(entry);
                if (AssetDatabase.IsValidFolder(entry.folder))
                    menu.AddItem(new GUIContent(label), false,
                        () => OpenSavedSearchInNewTab(entry.folder, entry.search));
                else
                    menu.AddDisabledItem(new GUIContent(label));
            }
            menu.AddSeparator("");
            var current = _session.ActiveTab?.CurrentPath;
            if (current != null && !TabstepBookmarks.Contains(current))
                menu.AddItem(new GUIContent("Add Current Folder"), false, () => TabstepBookmarks.Add(current));
            else
                menu.AddDisabledItem(new GUIContent("Add Current Folder"));
            var search = _session.ActiveTab?.SearchText;
            if (current != null && !string.IsNullOrWhiteSpace(search) &&
                !TabstepBookmarks.ContainsSearch(current, search))
                menu.AddItem(new GUIContent("Save Current Search"), false,
                    () => TabstepBookmarks.AddSearch(current, search));
            else
                menu.AddDisabledItem(new GUIContent("Save Current Search"));
            foreach (var folder in TabstepBookmarks.Folders)
            {
                var path = folder;
                menu.AddItem(new GUIContent("Remove/" + MenuPath(path)), false,
                    () => TabstepBookmarks.Remove(path));
            }
            foreach (var saved in TabstepBookmarks.Searches)
            {
                var entry = saved;
                menu.AddItem(new GUIContent("Remove/" + SavedSearchLabel(entry)), false,
                    () => TabstepBookmarks.RemoveSearch(entry));
            }
            menu.DropDown(dropRect);
        }

        static string SavedSearchLabel(SavedSearch entry)
        {
            return $"“{entry.search}”  in {MenuPath(entry.folder)}";
        }

        /// <summary>
        /// Every tab as a dropdown — the escape hatch when the bar overflows — plus
        /// the workspace menu (named tab sets that can be saved and restored).
        /// </summary>
        void DrawTabListButton(Rect rect)
        {
            var content = new GUIContent("▾", "All tabs / workspaces");
            // PopupWindow.Show needs a live OnGUI for its screen-space math, so the
            // menu item only requests the prompt and it opens on the next pass here.
            if (_openWorkspacePopup && Event.current.type == EventType.Repaint)
            {
                _openWorkspacePopup = false;
                PopupWindow.Show(rect, new WorkspaceNamePopup { _owner = this });
            }
            if (!EditorGUI.DropdownButton(rect, content, FocusType.Passive, EditorStyles.toolbarButton))
                return;
            var menu = new GenericMenu();
            for (int i = 0; i < _session.Count; i++)
            {
                int index = i;
                menu.AddItem(new GUIContent(MenuPath(_session.Tabs[i].CurrentPath ?? "New Tab")),
                    i == _session.ActiveIndex, () => ActivateTab(index));
            }
            menu.AddSeparator("");
            foreach (var name in TabstepWorkspaces.Names)
            {
                var workspaceName = name;
                menu.AddItem(new GUIContent("Workspaces/" + workspaceName), false,
                    () => LoadWorkspace(workspaceName));
            }
            if (TabstepWorkspaces.Names.Count > 0)
                menu.AddSeparator("Workspaces/");
            menu.AddItem(new GUIContent("Workspaces/Save Tabs As..."), false, () =>
            {
                _openWorkspacePopup = true;
                Repaint();
            });
            foreach (var name in TabstepWorkspaces.Names)
            {
                var workspaceName = name;
                menu.AddItem(new GUIContent("Workspaces/Delete/" + workspaceName), false, () =>
                {
                    if (EditorUtility.DisplayDialog("Delete Workspace",
                            $"Delete the workspace \"{workspaceName}\"?", "Delete", "Cancel"))
                        TabstepWorkspaces.Delete(workspaceName);
                });
            }
            menu.DropDown(rect);
        }

        /// <summary>Replaces the current tabs with a stored workspace (after confirming).</summary>
        void LoadWorkspace(string name)
        {
            var session = TabstepWorkspaces.Get(name);
            if (session == null || session.Count == 0) return;
            if (!EditorUtility.DisplayDialog("Load Workspace",
                    $"Load the workspace \"{name}\"?\nThe current tabs will be replaced.",
                    "Load", "Cancel"))
                return;
            _session = session;
            _applyTabToBrowser = true;
            Repaint();
        }

        internal void SaveWorkspace(string name)
        {
            TabstepWorkspaces.Save(name, _session);
            ShowNotification(new GUIContent($"Workspace \"{name.Trim()}\" saved"));
        }

        /// <summary>
        /// Appears at the right of the tab bar only while assets are being dragged:
        /// dropping parks them on the shelf for a later hand-off instead of moving them.
        /// </summary>
        void DrawShelfDropZone(Rect rect)
        {
            var content = new GUIContent("▼ Shelf", "Drop here to park on the shelf");
            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                EditorStyles.toolbarButton.Draw(rect, content, hover, false, false, false);
                EditorGUI.DrawRect(rect, new Color(0.5f, 0.7f, 1f, hover ? 0.35f : 0.15f));
            }
            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && hover)
            {
                if (DragAndDrop.objectReferences.Length == 0) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    TabstepShelfWindow.ShowNear(this).AddObjects(DragAndDrop.objectReferences);
                }
                e.Use();
            }
        }
    }
}
