using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// A Project window with Windows Explorer style tabs: each tab remembers its own
    /// folder and back/forward history, with a breadcrumb address bar for quick jumps.
    /// The folder view itself is Unity's stock Project browser, embedded via
    /// <see cref="ProjectBrowserHost"/>, so search, thumbnails, drag &amp; drop and
    /// context menus behave exactly like the built-in window. With Harmony present
    /// (<see cref="ProjectBrowserPatcher"/>) the browser's own toolbar and path header
    /// disappear entirely: their create button and search field move into the
    /// navigation bar, and the freed rows go to the folder view.
    ///
    /// Shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl(+Shift)+Tab cycle tabs,
    /// Alt+Left/Right or mouse side buttons back/forward, Alt+Up parent folder,
    /// Ctrl+L / Alt+D edit the path, Ctrl+F focus the search field.
    /// </summary>
    public class TabstepProjectWindow : EditorWindow
    {
        const string PathFieldControl = "Tabstep.PathField";

        // Tabs (with history) survive domain reloads and editor restarts via window serialization.
        [SerializeField] TabSession _session = new TabSession();

        ProjectBrowserHost _host;
        // Folder the embedded browser showed last frame; lets us tell apart "user navigated
        // inside the browser" (record into history) from "we just pointed the browser somewhere".
        string _observedBrowserPath;
        bool _applyTabToBrowser;

        // Explorer-style address bar: a sunken field showing the path as breadcrumbs,
        // flipping into a text field for typing/pasting a path.
        bool _editingPath;
        bool _focusPathField;
        string _pathEditText = "";

        // Search field in the navigation bar (only with the Harmony patches active).
        // Created lazily inside OnGUI: the SearchField constructor grabs a permanent
        // control id, which throws when run from a field initializer during window
        // deserialization. The browser owns the filter; _lastAppliedSearch tracks its
        // normalized echo of our last SetSearch so external changes can be told apart
        // from our own.
        SearchField _searchField;
        string _searchText = "";
        string _lastAppliedSearch;

        const float CrumbSeparatorWidth = 12f;
        static GUIStyle _crumbStyle;
        static GUIStyle _crumbCurrentStyle;
        static GUIStyle _crumbSeparatorStyle;

        static GUIStyle CrumbStyle => _crumbStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(5, 5, 0, 0),
        };

        static GUIStyle CrumbCurrentStyle => _crumbCurrentStyle ??= new GUIStyle(CrumbStyle)
        {
            fontStyle = FontStyle.Bold,
        };

        static GUIStyle CrumbSeparatorStyle => _crumbSeparatorStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.gray },
        };

        static readonly Color CrumbHoverColor = new Color(0.5f, 0.7f, 1f, 0.25f);

        // Drag & drop onto tab headers: hovering spring-loads (switches to) the tab,
        // dropping moves the dragged assets into that tab's folder.
        const double SpringLoadDelay = 0.6;
        int _dragHoverTab = -1;
        double _dragHoverStart;

        [MenuItem("YozoLab/Tabstep")]
        public static TabstepProjectWindow Open()
        {
            var window = GetWindow<TabstepProjectWindow>();
            window.minSize = new Vector2(400, 250);
            window.Show();
            window.Focus();
            return window;
        }

        [MenuItem("Assets/Open in Tabstep", false, 20)]
        static void OpenSelectionInTab()
        {
            var path = GetSelectedFolderPath();
            Open().OpenInNewTab(path);
        }

        /// <summary>Selected folder, or the folder containing the selected asset.</summary>
        static string GetSelectedFolderPath()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path)) return null;
            if (!AssetDatabase.IsValidFolder(path)) path = ProjectPaths.GetParent(path);
            return AssetDatabase.IsValidFolder(path) ? path : null;
        }

        /// <summary>Opens <paramref name="folderPath"/> (or the default folder) as a new active tab.</summary>
        public void OpenInNewTab(string folderPath)
        {
            _session.OpenTab(ValidFolderOrDefault(folderPath));
            _applyTabToBrowser = true;
            Repaint();
        }

        static string ValidFolderOrDefault(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath != null && AssetDatabase.IsValidFolder(folderPath)) return folderPath;
            var preferred = ProjectPaths.Normalize(TabstepSettings.NewTabFolder);
            if (preferred != null && AssetDatabase.IsValidFolder(preferred)) return preferred;
            return ProjectPaths.AssetsRoot;
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Tabstep", EditorGUIUtility.IconContent("Project").image);
            wantsMouseMove = true; // crumb hover highlight in the address bar
            _host = new ProjectBrowserHost(this);
            if (_session.Count == 0)
                _session.OpenTab(ValidFolderOrDefault(null));
            _applyTabToBrowser = true;
        }

        void OnDisable()
        {
            _host?.Dispose();
            _host = null;
        }

        void OnGUI()
        {
            if (!ProjectBrowserHost.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "This Unity version changed the internal Project browser API that " +
                    "Tabstep relies on. Please check for an updated package version.",
                    MessageType.Warning);
                return;
            }

            if (Event.current.type == EventType.Layout)
                SyncWithBrowser();
            HandleShortcuts();
            HandleMouseNavigation();

            float toolbarHeight = EditorStyles.toolbar.fixedHeight;
            bool showNav = TabstepSettings.ShowNavigationBar;
            DrawTabBar();
            if (showNav) DrawNavigationBar();

            // Computed identically in every IMGUI pass (never via GUILayoutUtility.GetRect,
            // whose dummy Layout-pass rect would feed the embedded browser a 1px layout).
            float top = toolbarHeight * (showNav ? 2 : 1);
            var content = new Rect(0, top, position.width, position.height - top);
            if (content.height > 0)
                // The navigation bar replaces the browser's path header (and, when the
                // Harmony patches are active, its whole toolbar) — only while it's shown,
                // so a path display and search always remain available.
                _host.OnGUI(content, showNav);
        }

        // ---- browser <-> tab sync --------------------------------------------

        void SyncWithBrowser()
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;

            if (_applyTabToBrowser)
            {
                _applyTabToBrowser = false;
                if (!_host.ShowFolder(tab.CurrentPath))
                {
                    // Folder was deleted/renamed while the tab pointed at it.
                    tab.Reset(ValidFolderOrDefault(null));
                    _host.ShowFolder(tab.CurrentPath);
                }
                _observedBrowserPath = tab.CurrentPath;
                return;
            }

            // The user navigated inside the embedded browser (double-clicked a folder,
            // used the breadcrumb of the browser itself...) — record it in the tab.
            var browserPath = _host.GetActiveFolderPath();
            if (browserPath == null || browserPath == _observedBrowserPath) return;
            _observedBrowserPath = browserPath;
            if (browserPath == tab.CurrentPath) return;

            // The folder changed while another window had focus — the embedded browser
            // was driven from outside (ping from an Inspector object field, "Show in
            // Project"...). Open the destination as a new tab so the current tab keeps
            // its place, like Explorer; in-window navigation stays in the same tab.
            if (TabstepSettings.PingOpensNewTab && focusedWindow != this)
                _session.OpenTab(browserPath);
            else
                tab.Navigate(browserPath);
        }

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

            if (ctrl && e.keyCode == KeyCode.T)
            {
                OpenInNewTab(null);
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.W)
            {
                CloseTab(_session.ActiveIndex);
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
            Repaint();
        }

        void CommitPathEdit()
        {
            _editingPath = false;
            GUIUtility.keyboardControl = 0;
            var folder = ResolveExternalPath(_pathEditText, out var pingPath);
            if (folder == null)
            {
                ShowNotification(new GUIContent("Folder not found:\n" + _pathEditText.Trim()));
                return;
            }
            NavigateTo(folder);
            PingLater(pingPath);
        }

        // ---- tab bar -----------------------------------------------------------

        void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            for (int i = 0; i < _session.Count; i++)
            {
                if (DrawTab(i))
                {
                    // Structure changed mid-loop (a tab closed) — bail out of this pass.
                    EditorGUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
            }
            if (GUILayout.Button(new GUIContent("+", "New tab (Ctrl+T)"), EditorStyles.toolbarButton,
                    GUILayout.Width(26)))
                OpenInNewTab(null);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            HandleTabBarDragAndDrop(GUILayoutUtility.GetLastRect());
        }

        /// <summary>Draws one tab. Returns true when the tab was closed (layout is now stale).</summary>
        bool DrawTab(int index)
        {
            var tab = _session.Tabs[index];
            bool active = index == _session.ActiveIndex;
            string title = ProjectPaths.Ellipsize(
                ProjectPaths.GetDisplayName(tab.CurrentPath) ?? "(empty)",
                TabstepSettings.MaxTabTitleLength);
            // Trailing spaces reserve room for the close glyph drawn over the active tab.
            var content = new GUIContent(active ? title + "    " : title, tab.CurrentPath);

            var style = EditorStyles.toolbarButton;
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.MaxWidth(200));
            var closeRect = new Rect(rect.xMax - 18, rect.y + (rect.height - 16) / 2, 16, 16);

            HandleTabDrag(rect, index, tab);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                if (active && e.button == 0 && closeRect.Contains(e.mousePosition))
                {
                    e.Use();
                    CloseTab(index);
                    return true;
                }
                if (e.button == 2 && TabstepSettings.MiddleClickClosesTab)
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

            if (GUI.Toggle(rect, active, content, style) && !active)
                ActivateTab(index);
            if (active)
                GUI.Label(closeRect, new GUIContent("×", "Close tab (Ctrl+W)"), EditorStyles.miniLabel);
            return false;
        }

        void ShowTabContextMenu(int index)
        {
            var menu = new GenericMenu();
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
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Duplicate Tab"), false, () =>
            {
                _session.DuplicateTab(index);
                _applyTabToBrowser = true;
                Repaint();
            });
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

        /// <summary>Project-relative asset paths of the current drag (empty for scene-object drags).</summary>
        static List<string> DraggedProjectPaths()
        {
            var paths = new List<string>();
            foreach (var raw in DragAndDrop.paths)
            {
                var path = ProjectPaths.Normalize(raw);
                if (path == null) continue;
                if (AssetDatabase.IsValidFolder(path) || AssetDatabase.GetMainAssetTypeAtPath(path) != null)
                    paths.Add(path);
            }
            return paths;
        }

        void MoveAssetsTo(string targetFolder, List<string> paths)
        {
            if (!AssetDatabase.IsValidFolder(targetFolder)) return;
            int moved = 0;
            foreach (var path in paths)
            {
                if (path == targetFolder) continue;
                if (ProjectPaths.GetParent(path) == targetFolder) continue; // already there
                if (targetFolder.StartsWith(path + "/", StringComparison.Ordinal)) continue; // folder into its own child
                var destination = AssetDatabase.GenerateUniqueAssetPath(
                    targetFolder + "/" + ProjectPaths.GetDisplayName(path));
                var error = AssetDatabase.MoveAsset(path, destination);
                if (string.IsNullOrEmpty(error)) moved++;
                else Debug.LogWarning($"[Tabstep] Could not move '{path}': {error}");
            }
            if (moved > 0)
                ShowNotification(new GUIContent($"Moved {moved} asset{(moved == 1 ? "" : "s")} to {targetFolder}"));
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

        // ---- navigation bar ----------------------------------------------------

        void DrawNavigationBar()
        {
            var tab = _session.ActiveTab;
            // With the Harmony patches the browser's toolbar is gone; its create button
            // and search field live in this bar instead.
            bool integrated = ProjectBrowserPatcher.Active;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            using (new EditorGUI.DisabledScope(tab == null || !tab.CanGoBack))
                if (GUILayout.Button(new GUIContent("◀", "Back (Alt+Left)"), EditorStyles.toolbarButton,
                        GUILayout.Width(26)))
                    GoBack();
            using (new EditorGUI.DisabledScope(tab == null || !tab.CanGoForward))
                if (GUILayout.Button(new GUIContent("▶", "Forward (Alt+Right)"), EditorStyles.toolbarButton,
                        GUILayout.Width(26)))
                    GoForward();
            var parent = ProjectPaths.GetParent(tab?.CurrentPath);
            using (new EditorGUI.DisabledScope(parent == null || !AssetDatabase.IsValidFolder(parent)))
                if (GUILayout.Button(new GUIContent("▲", "Parent folder (Alt+Up)"), EditorStyles.toolbarButton,
                        GUILayout.Width(26)))
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

            EditorGUILayout.EndHorizontal();

            addressRect.y += 1;
            addressRect.height -= 3;
            DrawAddressBar(addressRect, tab);
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
            var icon = AssetDatabase.GetCachedIcon(tab.CurrentPath);
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
                    x = DrawCrumbSeparator(x, inner);
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

        float DrawCrumbSeparator(float x, Rect inner)
        {
            GUI.Label(new Rect(x, inner.y, CrumbSeparatorWidth, inner.height), "›", CrumbSeparatorStyle);
            return x + CrumbSeparatorWidth;
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
            return rect.xMax;
        }

        void DrawPathField(Rect rect)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == PathFieldControl)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    CommitPathEdit();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CancelPathEdit();
                }
            }

            GUI.SetNextControlName(PathFieldControl);
            _pathEditText = GUI.TextField(rect, _pathEditText, EditorStyles.textField);

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
            }
        }

        void ShowPathBarContextMenu()
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy Path"), false,
                () => EditorGUIUtility.systemCopyBuffer = tab.CurrentPath);
            menu.AddItem(new GUIContent("Copy Absolute Path"), false,
                () => EditorGUIUtility.systemCopyBuffer = ToAbsolutePath(tab.CurrentPath));
            if (ResolveExternalPath(EditorGUIUtility.systemCopyBuffer, out _) != null)
                menu.AddItem(new GUIContent("Paste Path"), false, PastePathIntoActiveTab);
            else
                menu.AddDisabledItem(new GUIContent("Paste Path"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Edit Path"), false, BeginPathEdit);
            menu.ShowAsContext();
        }

    }
}
