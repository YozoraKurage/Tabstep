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
    /// Shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl+Shift+T reopen closed tab,
    /// Ctrl(+Shift)+Tab cycle tabs, Ctrl+1..9 jump to a tab (9 = last),
    /// Alt+Left/Right or mouse side buttons back/forward, Alt+Up parent folder,
    /// Ctrl+L / Alt+D edit the path, Ctrl+F focus the search field,
    /// Ctrl+Shift+C copy the absolute path, Ctrl+Shift+D summon the shelf
    /// to the mouse (adding the selection). With WASD navigation enabled
    /// (Preferences) the bare W/S keys step the selection through the shown
    /// folder, D opens the selected folder/asset and A goes back.
    /// </summary>
    public partial class TabstepProjectWindow : EditorWindow
    {
        const string PathFieldControl = "Tabstep.PathField";

        // Tabs (with history) survive domain reloads and editor restarts via window serialization.
        [SerializeField] TabSession _session = new TabSession();

        ProjectBrowserHost _host;
        // Folder the embedded browser showed last frame; lets us tell apart "user navigated
        // inside the browser" (record into history) from "we just pointed the browser somewhere".
        string _observedBrowserPath;
        bool _applyTabToBrowser;

        // The opt-in type-column view, drawn over the browser's list pane when a tab uses it.
        // One per window (transient view state) so multiple windows never interfere.
        readonly AssetColumnView _columnView = new AssetColumnView();
        AssetColumnView.Host _columnHost;

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

        // Tab titles for this pass: display names, disambiguated with the parent folder
        // when several tabs share a name. Rebuilt at the top of every DrawTabBar.
        readonly List<string> _tabTitles = new List<string>();
        // Tab header rects (window coordinates) — drag reorder / drop hit-testing.
        readonly List<Rect> _tabRects = new List<Rect>();
        // Horizontal scroll of the tab strip when the tabs overflow the bar (mouse wheel).
        float _tabScroll;

        // Drag reorder: one window-level control captures the mouse; the dragged tab
        // swaps with a neighbour when the cursor crosses that neighbour's center.
        static readonly int TabReorderHash = "Tabstep.TabReorder".GetHashCode();
        int _reorderIndex = -1;
        float _reorderStartX;
        bool _reordering;

        // True while an asset drag is in flight over this window; shows the shelf drop zone.
        // The zone's visibility is latched at Layout (_dragZoneVisible) so the control
        // layout never changes between a Layout pass and the event passes that follow it.
        bool _dragActive;
        bool _dragZoneVisible;

        // A middle press in the embedded asset list was converted to a left click;
        // the matching release opens the now-selected folder in a new tab.
        bool _browserMiddleClickArmed;

        // Spring-loading the nav buttons: hovering ◀ ▶ ▲ with a drag in flight navigates
        // after a moment, like Explorer — so a drag can walk back through the history.
        int _navSpringTarget;
        double _navSpringStart;

        // Path autocomplete while editing (Ctrl+L): subfolder candidates under the typed
        // prefix. Events are handled before the embedded browser (so clicks win), the
        // dropdown is painted after it (so it draws on top).
        readonly List<string> _pathSuggestions = new List<string>();
        int _pathSuggestionIndex = -1;
        string _pathSuggestionQuery;
        Rect _pathFieldRect;
        const float SuggestionRowHeight = 18f;
        const int MaxPathSuggestions = 8;

        // Last completed asset move (drop on a tab header / shelf hand-off), so it can
        // be undone from the context menus. Intentionally not serialized.
        List<(string from, string to)> _lastMove = new List<(string, string)>();

        bool _openWorkspacePopup; // "Save Tabs As..." defers the popup to the next OnGUI

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
            _session.AddTab(new TabState(ValidFolderOrDefault(folderPath)),
                TabstepSettings.NewTabBesideActive);
            _applyTabToBrowser = true;
            Repaint();
        }

        /// <summary>Opens a folder in a new tab with a search filter already applied.</summary>
        void OpenSavedSearchInNewTab(string folderPath, string search)
        {
            var tab = _session.AddTab(new TabState(ValidFolderOrDefault(folderPath)),
                TabstepSettings.NewTabBesideActive);
            tab.SearchText = search;
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

        // ---- multi-window --------------------------------------------------------

        /// <summary>
        /// Creates an additional floating Tabstep window showing the given tabs.
        /// Unlike <see cref="Open"/> (which focuses the existing window) this always
        /// makes a new one — any number of Tabstep windows can coexist; each owns its
        /// own session and embedded browser.
        /// </summary>
        static TabstepProjectWindow CreateNewWindow(TabSession session, Rect screenRect)
        {
            var window = CreateInstance<TabstepProjectWindow>();
            // OnEnable already ran (with an empty session, opening a default tab) —
            // the prepared session simply replaces it before the first OnGUI.
            window._session = session;
            window._applyTabToBrowser = true;
            window.minSize = new Vector2(400, 250);
            // Show(): a regular floating editor window — dockable, saved into the layout.
            window.Show();
            window.position = screenRect;
            window.Focus();
            return window;
        }

        /// <summary>Opens a copy of the tab (history included) in a new window.</summary>
        void OpenTabInNewWindow(int index)
        {
            if (index < 0 || index >= _session.Count) return;
            var session = new TabSession();
            session.Add(_session.Tabs[index].Clone());
            CreateNewWindow(session, CascadePosition());
        }

        /// <summary>
        /// Moves the tab (history included) out into its own window, docked into the
        /// layout right beside this one — as if its tab had been dropped on this
        /// pane's right edge. When docking is unavailable the new window stays
        /// floating at this window's right side instead.
        /// </summary>
        void SeparateTab(int index)
        {
            if (_session.Count <= 1) return; // moving the only tab would just relocate the window
            var tab = _session.DetachTab(index);
            if (tab == null) return;
            tab.Pinned = false; // the pin belongs to the old window's slot
            var session = new TabSession();
            session.Add(tab);
            var window = CreateNewWindow(session, RightSidePosition());
            WindowDocking.DockRightOf(this, window);
            _applyTabToBrowser = true;
            Repaint();
        }

        /// <summary>Clones this whole window — every tab, pins and the active tab included.</summary>
        void DuplicateWindow()
        {
            CreateNewWindow(_session.Clone(), CascadePosition());
        }

        Rect CascadePosition()
        {
            return ClampToMainWindow(new Rect(
                position.x + 28f, position.y + 28f, position.width, position.height));
        }

        Rect RightSidePosition()
        {
            return ClampToMainWindow(new Rect(
                position.xMax + 6f, position.y, position.width, position.height));
        }

        /// <summary>Keeps enough of a new window over the main window to stay grabbable.</summary>
        static Rect ClampToMainWindow(Rect rect)
        {
            var main = EditorGUIUtility.GetMainWindowPosition();
            rect.x = Mathf.Clamp(rect.x, main.x - rect.width + 80f, Mathf.Max(main.x, main.xMax - 80f));
            rect.y = Mathf.Clamp(rect.y, main.y, Mathf.Max(main.y, main.yMax - 80f));
            return rect;
        }

        // New tabs open in the type-column view when the Harmony compact layout is available,
        // and fall back to Unity's stock list otherwise. Existing (serialized) tabs keep the
        // mode they were saved with — only freshly created tabs follow this default.
        [InitializeOnLoadMethod]
        static void RegisterTabDefaults()
        {
            TabState.DefaultViewModeProvider = () =>
                ProjectBrowserPatcher.Active ? ItemViewMode.TypeColumns : ItemViewMode.Stock;
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Tabstep", EditorGUIUtility.IconContent("Project").image);
            wantsMouseMove = true; // crumb hover highlight in the address bar
            _host = new ProjectBrowserHost(this);
            _columnHost = new AssetColumnView.Host
            {
                OpenFolder = NavigateActiveTab,
                OpenFolderInNewTab = OpenInNewTab,
                Repaint = Repaint,
                MarkBrowserInteracted = _host.MarkAsLastInteracted,
            };
            if (_session.Count == 0)
                _session.OpenTab(ValidFolderOrDefault(null));
            _applyTabToBrowser = true;
            // A folder's contents changing while shown should refresh the type-column view.
            EditorApplication.projectChanged += OnProjectChanged;
        }

        void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            _host?.Dispose();
            _host = null;
        }

        void OnProjectChanged()
        {
            AssetColumnView.ProjectVersion++;
            Repaint();
        }

        void OnDestroy()
        {
            // The shelf belongs to the Tabstep windows unless the user pinned it; it
            // closes with the last of them. (OnDestroy, not OnDisable: domain reloads
            // must not close the shelf.)
            if (!TabstepShelfWindow.IsOpen || TabstepShelfWindow.Instance.KeepOpen) return;
            foreach (var window in Resources.FindObjectsOfTypeAll<TabstepProjectWindow>())
                if (window != this) return;
            TabstepShelfWindow.Instance.Close();
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
            TrackDragState();
            HandleShortcuts();
            HandleMouseNavigation();

            float toolbarHeight = EditorStyles.toolbar.fixedHeight;
            bool showNav = TabstepSettings.ShowNavigationBar;
            bool showStatus = TabstepSettings.ShowStatusBar;
            DrawTabBar();
            if (showNav) DrawNavigationBar();

            // The suggestion dropdown overlaps the browser: its events must win before
            // the browser runs, while its pixels must land after (drawn below).
            HandlePathSuggestionEvents();

            // Computed identically in every IMGUI pass (never via GUILayoutUtility.GetRect,
            // whose dummy Layout-pass rect would feed the embedded browser a 1px layout).
            float top = toolbarHeight * (showNav ? 2 : 1);
            float statusHeight = showStatus ? 18f : 0f;
            var content = new Rect(0, top, position.width, position.height - top - statusHeight);
            if (content.height > 0)
            {
                // The active tab can replace the browser's list pane with the type-column
                // view. It is laid over the (still painted) browser so the folder tree,
                // search and selection sync keep working — only the list pane is covered.
                var activeTab = _session.ActiveTab;
                bool columns = activeTab != null
                    && activeTab.ViewMode == ItemViewMode.TypeColumns
                    && !_host.IsSearching();
                Rect listRect = default;
                if (columns)
                {
                    var list = _host.GetListAreaRect();
                    if (list.width > 1f && list.height > 1f)
                        listRect = new Rect(content.x + list.x, content.y + list.y, list.width, list.height);
                    else
                        columns = false; // browser not laid out yet — fall back this frame
                }

                bool middleClickReleased = false;
                // The column view consumes its events before the covered browser sees them;
                // the stock middle-click conversion is only needed for the stock list.
                if (columns)
                    _columnView.HandleEvents(listRect, activeTab.CurrentPath,
                        activeTab.SortKey, activeTab.SortDescending, _columnHost);
                else
                    middleClickReleased = ConvertBrowserMiddleClick(content);

                // The navigation bar replaces the browser's path header (and, when the
                // Harmony patches are active, its whole toolbar) — only while it's shown,
                // so a path display and search always remain available.
                _host.OnGUI(content, showNav);

                // Drawn after the browser so the type columns land on top of the list pane.
                if (columns)
                    _columnView.Draw(listRect, activeTab.CurrentPath,
                        activeTab.SortKey, activeTab.SortDescending);
                if (middleClickReleased) OpenSelectedFolderInNewTab();
            }
            if (showStatus)
                DrawStatusBar(new Rect(0, position.height - statusHeight, position.width, statusHeight));
            DrawPathSuggestions();
        }

        /// <summary>
        /// Middle-click on a folder in the embedded asset list opens it in a new tab.
        /// The browser has no middle-click behaviour of its own, so the press is
        /// converted into a left click — which makes the browser select whatever is
        /// under the cursor — and the release reads that selection back. Restricted to
        /// the list area: tree clicks navigate by themselves and must stay untouched.
        /// Returns true on the (converted) release, after which the caller — once the
        /// browser has processed the click — opens the selected folder.
        /// </summary>
        bool ConvertBrowserMiddleClick(Rect content)
        {
            var e = Event.current;
            if (e.button != 2 || (e.type != EventType.MouseDown && e.type != EventType.MouseUp))
                return false;
            var list = _host.GetListAreaRect();
            if (list.width <= 0f) return false;
            var listRect = new Rect(content.x + list.x, content.y + list.y, list.width, list.height);
            if (e.type == EventType.MouseDown)
            {
                // Only a press that starts in the list arms the release.
                _browserMiddleClickArmed = listRect.Contains(e.mousePosition);
                if (_browserMiddleClickArmed) e.button = 0;
                return false;
            }
            if (!_browserMiddleClickArmed) return false;
            _browserMiddleClickArmed = false;
            if (!listRect.Contains(e.mousePosition)) return false;
            e.button = 0;
            return true;
        }

        void OpenSelectedFolderInNewTab()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                OpenInNewTab(path);
        }

        /// <summary>Navigates the active tab into a folder (double-click in the type-column view).</summary>
        void NavigateActiveTab(string folderPath)
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;
            tab.Navigate(folderPath);
            _applyTabToBrowser = true;
            Repaint();
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
                // The browser is shared between tabs, so each tab carries its own
                // search filter and gets it back when it becomes active again.
                var saved = tab.SearchText ?? "";
                if ((_host.GetSearchText() ?? "") != saved)
                    _host.SetSearch(saved);
                _searchText = saved;
                _lastAppliedSearch = _host.GetSearchText() ?? saved;
                return;
            }

            // Mirror the browser's live filter into the active tab (typed into either
            // search field, or cleared by the browser when a folder is clicked).
            var browserSearch = _host.GetSearchText();
            if (browserSearch != null)
                tab.SearchText = browserSearch;

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
            {
                // If this window already has a tab on the pinged folder, switch to it
                // instead of opening a duplicate.
                int existing = _session.IndexOfFolder(browserPath);
                if (existing >= 0)
                    ActivateTab(existing);
                else
                    _session.OpenTab(browserPath);
            }
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
            var items = FolderItems(folder);
            var current = ProjectPaths.Normalize(AssetDatabase.GetAssetPath(Selection.activeObject));
            var next = NextSelectionPath(items, current, delta);
            if (next == null) return;
            var asset = AssetDatabase.LoadMainAssetAtPath(next);
            if (asset == null) return;
            Selection.activeObject = asset;
            _host.FrameObject(asset.GetInstanceID());
            Repaint();
        }

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
        static List<string> FolderItems(string folder)
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

        // ---- path autocomplete ---------------------------------------------------

        void ClearPathSuggestions()
        {
            _pathSuggestions.Clear();
            _pathSuggestionIndex = -1;
            _pathSuggestionQuery = null;
        }

        /// <summary>Subfolders under the typed prefix; the segment after the last '/' filters them.</summary>
        void UpdatePathSuggestions()
        {
            _pathSuggestionQuery = _pathEditText;
            _pathSuggestions.Clear();
            _pathSuggestionIndex = -1;
            var text = (_pathEditText ?? "").Trim().Trim('"').Replace('\\', '/');
            int slash = text.LastIndexOf('/');
            if (slash < 0)
            {
                foreach (var root in new[] { ProjectPaths.AssetsRoot, "Packages" })
                    if (root.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                        !root.Equals(text, StringComparison.OrdinalIgnoreCase))
                        _pathSuggestions.Add(root);
                return;
            }
            var parent = text.Substring(0, slash);
            var partial = text.Substring(slash + 1);
            // "Packages" is not a folder asset itself but GetSubFolders still lists packages.
            if (parent != "Packages" && !AssetDatabase.IsValidFolder(parent)) return;
            foreach (var sub in AssetDatabase.GetSubFolders(parent))
            {
                var path = ProjectPaths.Normalize(sub);
                var name = ProjectPaths.GetDisplayName(path);
                if (name == null) continue;
                if (partial.Length > 0 && !name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
                _pathSuggestions.Add(path);
                if (_pathSuggestions.Count >= MaxPathSuggestions) break;
            }
        }

        /// <summary>Tab completion: the suggestion becomes the text, ready for the next segment.</summary>
        void AcceptSuggestionIntoText(string path)
        {
            _pathEditText = path + "/";
            MovePathCursorToEnd();
            UpdatePathSuggestions();
            Repaint();
        }

        /// <summary>Clicking or Enter on a suggestion navigates there and ends the edit.</summary>
        void CommitSuggestion(string path)
        {
            _editingPath = false;
            GUIUtility.keyboardControl = 0;
            ClearPathSuggestions();
            NavigateTo(path);
        }

        /// <summary>Completing must not leave the old text selected — put the caret at the end.</summary>
        void MovePathCursorToEnd()
        {
            if (GUIUtility.keyboardControl == 0) return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = _pathEditText;
            editor.cursorIndex = editor.selectIndex = _pathEditText.Length;
        }

        Rect PathSuggestionBoxRect()
        {
            return new Rect(_pathFieldRect.x, _pathFieldRect.yMax + 1, _pathFieldRect.width,
                _pathSuggestions.Count * SuggestionRowHeight + 2);
        }

        Rect PathSuggestionRowRect(int index)
        {
            var box = PathSuggestionBoxRect();
            return new Rect(box.x + 1, box.y + 1 + index * SuggestionRowHeight,
                box.width - 2, SuggestionRowHeight);
        }

        /// <summary>
        /// Mouse interaction with the dropdown. Runs before the embedded browser so the
        /// clicks never reach the folder view underneath the overlay.
        /// </summary>
        void HandlePathSuggestionEvents()
        {
            if (!_editingPath || _pathSuggestions.Count == 0) return;
            var e = Event.current;
            var box = PathSuggestionBoxRect();
            if (e.type == EventType.MouseMove && box.Contains(e.mousePosition))
            {
                _pathSuggestionIndex = Mathf.Clamp(
                    (int)((e.mousePosition.y - box.y - 1) / SuggestionRowHeight),
                    0, _pathSuggestions.Count - 1);
                Repaint();
            }
            else if (e.type == EventType.MouseDown && box.Contains(e.mousePosition))
            {
                int row = Mathf.Clamp((int)((e.mousePosition.y - box.y - 1) / SuggestionRowHeight),
                    0, _pathSuggestions.Count - 1);
                var path = _pathSuggestions[row];
                e.Use();
                if (e.button == 2)
                {
                    // Middle-click: new tab, like everywhere else in the window.
                    _editingPath = false;
                    GUIUtility.keyboardControl = 0;
                    ClearPathSuggestions();
                    OpenInNewTab(path);
                }
                else
                {
                    CommitSuggestion(path);
                }
            }
        }

        /// <summary>Painted at the very end of OnGUI so it overlays the folder view.</summary>
        void DrawPathSuggestions()
        {
            if (!_editingPath || _pathSuggestions.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;
            var box = PathSuggestionBoxRect();
            var border = EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.4f, 0.4f, 0.4f);
            var background = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.9f, 0.9f, 0.9f);
            EditorGUI.DrawRect(box, border);
            EditorGUI.DrawRect(new Rect(box.x + 1, box.y + 1, box.width - 2, box.height - 2), background);
            for (int i = 0; i < _pathSuggestions.Count; i++)
            {
                var row = PathSuggestionRowRect(i);
                if (i == _pathSuggestionIndex)
                    EditorGUI.DrawRect(row, new Color(0.24f, 0.49f, 0.91f, 0.5f));
                var icon = AssetDatabase.GetCachedIcon(_pathSuggestions[i]);
                if (icon != null)
                    GUI.DrawTexture(new Rect(row.x + 3, row.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(row.x + 22, row.y, row.width - 24, row.height),
                    _pathSuggestions[i], EditorStyles.label);
            }
        }




        void OnSelectionChange()
        {
            _statusSelectionTime = 0; // recompute on the next repaint
            Repaint();
        }

        void ShowPathBarContextMenu()
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;
            var menu = new GenericMenu();
            if (tab.CurrentPath != null)
            {
                menu.AddItem(new GUIContent("Copy Path"), false,
                    () => EditorGUIUtility.systemCopyBuffer = tab.CurrentPath);
                menu.AddItem(new GUIContent("Copy Absolute Path"), false,
                    () => EditorGUIUtility.systemCopyBuffer = ToAbsolutePath(tab.CurrentPath));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Copy Path"));
                menu.AddDisabledItem(new GUIContent("Copy Absolute Path"));
            }
            if (ResolveExternalPath(EditorGUIUtility.systemCopyBuffer, out _) != null)
                menu.AddItem(new GUIContent("Paste Path"), false, PastePathIntoActiveTab);
            else
                menu.AddDisabledItem(new GUIContent("Paste Path"));
            menu.AddSeparator("");
            if (_lastMove.Count > 0)
                menu.AddItem(new GUIContent("Undo Last Asset Move"), false, UndoLastMove);
            else
                menu.AddDisabledItem(new GUIContent("Undo Last Asset Move"));
            menu.AddItem(new GUIContent("Edit Path"), false, BeginPathEdit);
            menu.ShowAsContext();
        }

    }
}
