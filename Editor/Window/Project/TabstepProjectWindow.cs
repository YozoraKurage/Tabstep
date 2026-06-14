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
    internal sealed partial class TabstepProjectWindow : EditorWindow
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
