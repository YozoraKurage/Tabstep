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
        // ---- navigation bar ----------------------------------------------------

        void DrawNavigationBar()
        {
            var tab = _session.ActiveTab;
            // With the Harmony patches the browser's toolbar is gone; its create button
            // and search field live in this bar instead.
            bool integrated = ProjectBrowserPatcher.Active;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Back/forward/up get explicit rects: right-clicking lists the history,
            // middle-clicking opens the target in a new tab, and hovering with a drag
            // in flight spring-loads the navigation — all even while the button itself
            // is disabled or busy.
            var backContent = new GUIContent("◀", "Back (Alt+Left)\nRight-click: history\nMiddle-click: open in new tab");
            var backRect = GUILayoutUtility.GetRect(backContent, EditorStyles.toolbarButton,
                GUILayout.Width(26));
            HandleHistoryMenuClick(backRect, tab);
            HandleNavMiddleClick(backRect, tab != null && tab.CanGoBack
                ? tab.History[tab.HistoryIndex - 1] : null);
            HandleNavSpringLoad(backRect, 1, tab != null && tab.CanGoBack, GoBack);
            using (new EditorGUI.DisabledScope(tab == null || !tab.CanGoBack))
                if (GUI.Button(backRect, backContent, EditorStyles.toolbarButton))
                    GoBack();
            var forwardContent = new GUIContent("▶", "Forward (Alt+Right)\nRight-click: history\nMiddle-click: open in new tab");
            var forwardRect = GUILayoutUtility.GetRect(forwardContent, EditorStyles.toolbarButton,
                GUILayout.Width(26));
            HandleHistoryMenuClick(forwardRect, tab);
            HandleNavMiddleClick(forwardRect, tab != null && tab.CanGoForward
                ? tab.History[tab.HistoryIndex + 1] : null);
            HandleNavSpringLoad(forwardRect, 2, tab != null && tab.CanGoForward, GoForward);
            using (new EditorGUI.DisabledScope(tab == null || !tab.CanGoForward))
                if (GUI.Button(forwardRect, forwardContent, EditorStyles.toolbarButton))
                    GoForward();
            var parent = ProjectPaths.GetParent(tab?.CurrentPath);
            bool canGoUp = parent != null && AssetDatabase.IsValidFolder(parent);
            var upContent = new GUIContent("▲", "Parent folder (Alt+Up)\nMiddle-click: open in new tab");
            var upRect = GUILayoutUtility.GetRect(upContent, EditorStyles.toolbarButton,
                GUILayout.Width(26));
            HandleNavMiddleClick(upRect, canGoUp ? parent : null);
            HandleNavSpringLoad(upRect, 3, canGoUp, GoUp);
            using (new EditorGUI.DisabledScope(!canGoUp))
                if (GUI.Button(upRect, upContent, EditorStyles.toolbarButton))
                    GoUp();

            if (integrated) DrawCreateButton();

            GUILayout.Space(4);
            // The middle of the row is the Explorer-style address bar.
            var addressRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(EditorStyles.toolbar.fixedHeight));
            GUILayout.Space(4);

            if (integrated)
            {
                DrawSearchField();
                GUILayout.Space(2);
            }

            DrawViewModeControls();
            DrawShelfToggle();

            EditorGUILayout.EndHorizontal();

            addressRect.y += 1;
            addressRect.height -= 3;
            DrawAddressBar(addressRect, tab);
        }

        void HandleHistoryMenuClick(Rect rect, TabState tab)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 1 || !rect.Contains(e.mousePosition))
                return;
            e.Use();
            ShowHistoryMenu(rect, tab);
        }

        /// <summary>Middle-clicking a nav button opens its destination in a new tab.</summary>
        void HandleNavMiddleClick(Rect rect, string destination)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 2 || !rect.Contains(e.mousePosition))
                return;
            e.Use();
            if (destination != null && AssetDatabase.IsValidFolder(destination))
                OpenInNewTab(destination);
        }

        /// <summary>
        /// Hovering a nav button with a drag in flight navigates after a moment (and
        /// keeps navigating step by step), like Explorer — so a drag started deep in
        /// one folder can walk back through the history to its target.
        /// </summary>
        void HandleNavSpringLoad(Rect rect, int id, bool canNavigate, Action navigate)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated)
            {
                if (_navSpringTarget == id && e.type == EventType.DragExited)
                    _navSpringTarget = 0;
                return;
            }
            if (!rect.Contains(e.mousePosition))
            {
                if (_navSpringTarget == id) _navSpringTarget = 0;
                return;
            }
            if (!canNavigate) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            if (_navSpringTarget != id)
            {
                _navSpringTarget = id;
                _navSpringStart = EditorApplication.timeSinceStartup;
            }
            else if (EditorApplication.timeSinceStartup - _navSpringStart > SpringLoadDelay)
            {
                navigate();
                _navSpringStart = EditorApplication.timeSinceStartup; // step again after another delay
            }
            Repaint(); // keep the timer ticking
            e.Use();
        }

        static readonly string[] SortKeyLabels = { "Name", "Type", "Date", "Size" };

        /// <summary>
        /// Toggle between Unity's stock list and the type-column view, plus that view's sort
        /// controls. Lives next to the search field where the filter chips used to be.
        ///
        /// The sort controls are drawn to the LEFT of the toggle so that showing or hiding
        /// them never moves the toggle: everything to the toggle's right (only the shelf
        /// button) is fixed width, while the flexible address bar on its left absorbs the
        /// change. So the toggle stays under the cursor when it is pressed.
        /// </summary>
        void DrawViewModeControls()
        {
            var tab = _session.ActiveTab;
            bool columns = tab != null && tab.ViewMode == ItemViewMode.TypeColumns;

            if (columns)
            {
                int key = EditorGUILayout.Popup((int)tab.SortKey, SortKeyLabels,
                    EditorStyles.toolbarPopup, GUILayout.Width(70));
                if (key != (int)tab.SortKey)
                {
                    tab.SortKey = (AssetSortKey)key;
                    _columnView.MarkDirty();
                    Repaint();
                }

                var dir = new GUIContent(tab.SortDescending ? "↓" : "↑",
                    tab.SortDescending ? "Sort: descending — click for ascending"
                                       : "Sort: ascending — click for descending");
                if (GUILayout.Button(dir, EditorStyles.toolbarButton, GUILayout.Width(24)))
                {
                    tab.SortDescending = !tab.SortDescending;
                    _columnView.MarkDirty();
                    Repaint();
                }
            }

            using (new EditorGUI.DisabledScope(tab == null))
            {
                bool now = GUILayout.Toggle(columns, new GUIContent("☰",
                        columns
                            ? "Type-column view (items grouped by type) — click for Unity's standard list"
                            : "Unity's standard list — click for the type-column view (items grouped by type)"),
                    EditorStyles.toolbarButton, GUILayout.Width(26));
                if (tab != null && now != columns)
                {
                    tab.ViewMode = now ? ItemViewMode.TypeColumns : ItemViewMode.Stock;
                    _columnView.MarkDirty();
                    Repaint();
                }
            }
        }

        /// <summary>Right-clicking back/forward lists the whole history, newest first.</summary>
        void ShowHistoryMenu(Rect dropRect, TabState tab)
        {
            if (tab == null || tab.History.Count == 0) return;
            var menu = new GenericMenu();
            for (int i = tab.History.Count - 1; i >= 0; i--)
            {
                int index = i;
                menu.AddItem(new GUIContent(MenuPath(tab.History[i])), i == tab.HistoryIndex, () =>
                {
                    tab.GoToHistoryIndex(index);
                    _applyTabToBrowser = true;
                    Repaint();
                });
            }
            menu.DropDown(dropRect);
        }

        void DrawShelfToggle()
        {
            bool open = TabstepShelfWindow.IsOpen;
            bool now = GUILayout.Toggle(open,
                new GUIContent("Shelf",
                    "The shelf — a temporary tray for assets in transit between tabs, " +
                    "Inspector fields and the scene. Ctrl+Shift+D summons it to the " +
                    "mouse and adds the selection."),
                EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (now != open) TabstepShelfWindow.Toggle(this);
        }

        /// <summary>The stock toolbar's "+" dropdown: the Assets/Create menu.</summary>
        void DrawCreateButton()
        {
            var content = new GUIContent(EditorGUIUtility.IconContent("CreateAddNew"))
            {
                tooltip = "Create assets in the current folder",
            };
            var style = GUI.skin.FindStyle("ToolbarCreateAddNewDropDown") ?? EditorStyles.toolbarDropDown;
            // Natural content size only — the style stretches, and unlike the stock
            // toolbar there is no FlexibleSpace here to absorb the slack.
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(false));
            if (EditorGUI.DropdownButton(rect, content, FocusType.Passive, style))
            {
                GUIUtility.hotControl = 0;
                // Create menu items target the last interacted Project browser's folder —
                // make sure that's ours, since this click never reaches the browser.
                _host.MarkAsLastInteracted();
                // ProjectWindowUtil.GetActiveFolderPath falls back to Selection.activeObject
                // in two-column mode whenever the tree pane lacks keyboard focus (it
                // always does here — the column view covers it). Without anchoring the
                // selection to the shown folder, Create > X lands in whichever folder the
                // previously-selected asset lived in, leaving the column view empty and
                // the embedded browser holding an orphan inline-rename phantom.
                var here = _session.ActiveTab?.CurrentPath;
                if (!string.IsNullOrEmpty(here))
                {
                    var folderObj = AssetDatabase.LoadMainAssetAtPath(here);
                    if (folderObj != null && folderObj != Selection.activeObject)
                        Selection.activeObject = folderObj;
                }
                EditorUtility.DisplayPopupMenu(rect, "Assets/Create", null);
            }
        }

        /// <summary>
        /// The stock toolbar's search field, driving the embedded browser's filter
        /// (same "t: l: ..." syntax). The browser stays the source of truth.
        /// </summary>
        void DrawSearchField()
        {
            // Mirror external changes (the search header's clear button, scripts...)
            // unless they're just the normalized echo of our own SetSearch.
            var browserText = _host.GetSearchText();
            if (browserText != null && browserText != _lastAppliedSearch)
            {
                _searchText = browserText;
                _lastAppliedSearch = browserText;
            }

            _searchField ??= new SearchField();
            var edited = _searchField.OnToolbarGUI(_searchText,
                GUILayout.MinWidth(65), GUILayout.MaxWidth(300));
            if (edited == _searchText) return;
            _searchText = edited;
            _host.SetSearch(edited);
            // SetSearch round-trips the text through the filter; remember the normalized
            // form so next frame's mirror check doesn't stomp what the user is typing.
            _lastAppliedSearch = _host.GetSearchText() ?? edited;
            // The filter belongs to the tab: it comes back when the tab is next active.
            if (_session.ActiveTab != null)
                _session.ActiveTab.SearchText = edited;
        }

        /// <summary>
        /// Explorer-style address bar: a sunken text-field frame holding a folder icon
        /// and the path as hoverable, clickable breadcrumb segments. Clicking the empty
        /// area (or the current folder name) flips it into the editable text field;
        /// right-click offers copy/paste. Crumbs that don't fit collapse behind «.
        /// </summary>
        void DrawAddressBar(Rect rect, TabState tab)
        {
            var e = Event.current;
            if (e.type == EventType.MouseMove && rect.Contains(e.mousePosition))
                Repaint(); // keep the crumb hover highlight live

            if (_editingPath)
            {
                DrawPathField(rect);
                return;
            }

            // The sunken field frame is what makes the bar read as "this shows the path".
            if (e.type == EventType.Repaint)
                EditorStyles.textField.Draw(rect, GUIContent.none, false, false, false, false);
            if (tab == null) return;

            if (e.type == EventType.MouseDown && e.button == 1 && rect.Contains(e.mousePosition))
            {
                e.Use();
                ShowPathBarContextMenu();
                return;
            }

            var inner = new Rect(rect.x + 4, rect.y, rect.width - 8, rect.height);
            float x = inner.x;

            // Folder icon at the left, like Explorer's address bar.
            var icon = tab.CurrentPath != null ? AssetDatabase.GetCachedIcon(tab.CurrentPath) : null;
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, inner.y + (inner.height - 16) / 2, 16, 16), icon,
                    ScaleMode.ScaleToFit);
                x += 20;
            }

            var crumbs = ProjectPaths.GetBreadcrumbs(tab.CurrentPath);
            int firstVisible = FirstVisibleCrumb(crumbs, inner.xMax - x);
            if (firstVisible > 0)
                x = DrawHiddenCrumbsButton(crumbs, firstVisible, x, inner);
            for (int i = firstVisible; i < crumbs.Count; i++)
            {
                if (i > firstVisible || firstVisible > 0)
                    x = DrawCrumbSeparator(x, inner, crumbs[i - 1].path, crumbs[i].path);
                x = DrawCrumb(crumbs[i], i == crumbs.Count - 1, x, inner);
            }

            // The space after the last crumb edits the path, like clicking the empty
            // part of Explorer's address bar; the text cursor advertises it.
            var editRect = new Rect(x, inner.y, Mathf.Max(0, inner.xMax - x), inner.height);
            EditorGUIUtility.AddCursorRect(editRect, MouseCursor.Text);
            if (e.type == EventType.MouseDown && e.button == 0 && editRect.Contains(e.mousePosition))
            {
                e.Use();
                BeginPathEdit();
            }
        }

        /// <summary>First crumb that fits right-aligned; everything older collapses behind «.</summary>
        int FirstVisibleCrumb(List<(string name, string path)> crumbs, float available)
        {
            const float chevronWidth = 18f;
            float used = 0;
            int first = crumbs.Count - 1; // the current folder always shows
            for (int i = crumbs.Count - 1; i >= 0; i--)
            {
                var style = i == crumbs.Count - 1 ? CrumbCurrentStyle : CrumbStyle;
                float width = style.CalcSize(new GUIContent(crumbs[i].name)).x + CrumbSeparatorWidth;
                float reserved = i > 0 ? chevronWidth : 0;
                if (i < crumbs.Count - 1 && used + width + reserved > available) break;
                used += width;
                first = i;
            }
            return first;
        }

        float DrawHiddenCrumbsButton(List<(string name, string path)> crumbs, int hiddenCount, float x, Rect inner)
        {
            var rect = new Rect(x, inner.y, 18, inner.height);
            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.Repaint && hover)
                EditorGUI.DrawRect(rect, CrumbHoverColor);
            GUI.Label(rect, new GUIContent("«", "Folders that don't fit"), CrumbStyle);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                var menu = new GenericMenu();
                for (int i = hiddenCount - 1; i >= 0; i--)
                {
                    var path = crumbs[i].path;
                    if (AssetDatabase.IsValidFolder(path))
                        menu.AddItem(new GUIContent(crumbs[i].name), false, () => NavigateTo(path));
                    else
                        menu.AddDisabledItem(new GUIContent(crumbs[i].name));
                }
                menu.DropDown(rect);
            }
            return rect.xMax;
        }

        /// <summary>
        /// The › between crumbs is a control of its own, like Explorer's chevrons:
        /// clicking it lists the parent crumb's subfolders for a sideways jump.
        /// </summary>
        float DrawCrumbSeparator(float x, Rect inner, string parentPath, string currentChildPath)
        {
            var rect = new Rect(x, inner.y, CrumbSeparatorWidth, inner.height);
            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.Repaint && hover)
                EditorGUI.DrawRect(rect, CrumbHoverColor);
            GUI.Label(rect, new GUIContent("›", "Subfolders of " + parentPath), CrumbSeparatorStyle);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                ShowSubfolderMenu(rect, parentPath, currentChildPath);
            }
            return x + CrumbSeparatorWidth;
        }

        void ShowSubfolderMenu(Rect dropRect, string parentPath, string currentChildPath)
        {
            var menu = new GenericMenu();
            var subfolders = AssetDatabase.GetSubFolders(parentPath);
            if (subfolders.Length == 0)
                menu.AddDisabledItem(new GUIContent("No subfolders"));
            foreach (var sub in subfolders)
            {
                var path = ProjectPaths.Normalize(sub);
                menu.AddItem(new GUIContent(ProjectPaths.GetDisplayName(path)),
                    path == currentChildPath, () => NavigateTo(path));
            }
            menu.DropDown(dropRect);
        }

        float DrawCrumb((string name, string path) crumb, bool isCurrent, float x, Rect inner)
        {
            var style = isCurrent ? CrumbCurrentStyle : CrumbStyle;
            var content = new GUIContent(crumb.name, crumb.path);
            var rect = new Rect(x, inner.y, style.CalcSize(content).x, inner.height);
            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.Repaint && hover)
                EditorGUI.DrawRect(rect, CrumbHoverColor);
            GUI.Label(rect, content, style);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                // Clicking the current folder name selects the path for editing (Explorer);
                // any other segment jumps to that folder.
                if (isCurrent) BeginPathEdit();
                else if (AssetDatabase.IsValidFolder(crumb.path)) NavigateTo(crumb.path);
            }
            else if (e.type == EventType.MouseDown && e.button == 2 && hover)
            {
                // Middle-click opens the segment in a new tab, like a browser link.
                e.Use();
                if (AssetDatabase.IsValidFolder(crumb.path)) OpenInNewTab(crumb.path);
            }
            return rect.xMax;
        }

        void DrawPathField(Rect rect)
        {
            _pathFieldRect = rect; // anchors the autocomplete dropdown
            var e = Event.current;
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == PathFieldControl)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    // Enter on a highlighted suggestion takes the suggestion; otherwise
                    // the typed text is committed as-is.
                    if (_pathSuggestionIndex >= 0 && _pathSuggestionIndex < _pathSuggestions.Count)
                        CommitSuggestion(_pathSuggestions[_pathSuggestionIndex]);
                    else
                        CommitPathEdit();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CancelPathEdit();
                }
                else if (e.keyCode == KeyCode.Tab && _pathSuggestions.Count > 0)
                {
                    // Tab completes (IMGUI would otherwise move keyboard focus).
                    e.Use();
                    int pick = _pathSuggestionIndex >= 0 ? _pathSuggestionIndex : 0;
                    AcceptSuggestionIntoText(_pathSuggestions[pick]);
                }
                else if (e.keyCode == KeyCode.DownArrow && _pathSuggestions.Count > 0)
                {
                    e.Use();
                    _pathSuggestionIndex = (_pathSuggestionIndex + 1) % _pathSuggestions.Count;
                    Repaint();
                }
                else if (e.keyCode == KeyCode.UpArrow && _pathSuggestions.Count > 0)
                {
                    e.Use();
                    _pathSuggestionIndex = _pathSuggestionIndex <= 0
                        ? _pathSuggestions.Count - 1
                        : _pathSuggestionIndex - 1;
                    Repaint();
                }
            }

            GUI.SetNextControlName(PathFieldControl);
            _pathEditText = GUI.TextField(rect, _pathEditText, EditorStyles.textField);
            if (_pathEditText != _pathSuggestionQuery)
                UpdatePathSuggestions();

            if (_focusPathField)
            {
                EditorGUI.FocusTextInControl(PathFieldControl);
                if (e.type == EventType.Repaint) _focusPathField = false;
            }
            else if (_editingPath && e.type == EventType.Repaint &&
                     GUI.GetNameOfFocusedControl() != PathFieldControl)
            {
                // Focus moved elsewhere (clicked into the browser...) — revert to
                // breadcrumbs without navigating, like Explorer.
                _editingPath = false;
                ClearPathSuggestions();
            }
        }
    }
}
