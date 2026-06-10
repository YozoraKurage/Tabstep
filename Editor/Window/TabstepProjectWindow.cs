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
    /// to the mouse (adding the selection).
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

        // Tab titles for this pass: display names, disambiguated with the parent folder
        // when several tabs share a name. Rebuilt at the top of every DrawTabBar.
        readonly List<string> _tabTitles = new List<string>();
        // Tab header rects from the last Repaint pass — drag reorder hit-testing.
        readonly List<Rect> _tabRects = new List<Rect>();

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

        // Status bar caches — folder listings and file sizes are not free, so they
        // refresh on a timer instead of every repaint.
        string _statusFolder;
        int _statusItemCount;
        double _statusFolderTime;
        string _statusSelectionText = "";
        double _statusSelectionTime;

        bool _openWorkspacePopup; // "Save Tabs As..." defers the popup to the next OnGUI

        static GUIStyle _rightMiniLabel;
        static GUIStyle RightMiniLabel => _rightMiniLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };

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

        void OnDestroy()
        {
            // The shelf belongs to this window unless the user pinned it.
            // (OnDestroy, not OnDisable: domain reloads must not close the shelf.)
            if (TabstepShelfWindow.IsOpen && !TabstepShelfWindow.Instance.KeepOpen)
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
                bool middleClickReleased = ConvertBrowserMiddleClick(content);
                // The navigation bar replaces the browser's path header (and, when the
                // Harmony patches are active, its whole toolbar) — only while it's shown,
                // so a path display and search always remain available.
                _host.OnGUI(content, showNav);
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

        // ---- tab bar -----------------------------------------------------------

        void DrawTabBar()
        {
            BuildTabTitles();
            int reorderControl = GUIUtility.GetControlID(TabReorderHash, FocusType.Passive);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            for (int i = 0; i < _session.Count; i++)
            {
                if (DrawTab(i, reorderControl))
                {
                    // Structure changed mid-loop (a tab closed) — bail out of this pass.
                    EditorGUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
            }
            DrawNewTabButton();
            GUILayout.FlexibleSpace();
            DrawShelfDropZone();
            DrawTabListButton();
            EditorGUILayout.EndHorizontal();
            var barRect = GUILayoutUtility.GetLastRect();
            HandleTabReorder(reorderControl);
            HandleTabBarDragAndDrop(barRect);
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
        bool DrawTab(int index, int reorderControl)
        {
            var tab = _session.Tabs[index];
            bool active = index == _session.ActiveIndex;
            var style = EditorStyles.toolbarButton;

            GUIContent content;
            Rect rect;
            if (tab.Pinned)
            {
                // Pinned tabs shrink to their folder icon, like a browser's pinned tabs.
                var icon = tab.CurrentPath != null ? AssetDatabase.GetCachedIcon(tab.CurrentPath) : null;
                content = icon != null
                    ? new GUIContent(icon, tab.CurrentPath)
                    : new GUIContent(ProjectPaths.Ellipsize(_tabTitles[index], 4), tab.CurrentPath);
                rect = GUILayoutUtility.GetRect(content, style, GUILayout.Width(28));
            }
            else
            {
                string title = _tabTitles[index];
                // Trailing spaces reserve room for the close glyph drawn over the active tab.
                content = new GUIContent(active ? title + "    " : title, tab.CurrentPath);
                rect = GUILayoutUtility.GetRect(content, style, GUILayout.MaxWidth(200));
            }
            if (Event.current.type == EventType.Repaint && index < _tabRects.Count)
                _tabRects[index] = rect;
            var closeRect = new Rect(rect.xMax - 18, rect.y + (rect.height - 16) / 2, 16, 16);

            HandleTabDrag(rect, index, tab);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
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

            GUI.Toggle(rect, active, content, style); // visuals only; clicks are handled above
            if (active && !tab.Pinned)
                GUI.Label(closeRect, new GUIContent("×", "Close tab (Ctrl+W)"), EditorStyles.miniLabel);
            return false;
        }

        void DrawNewTabButton()
        {
            var content = new GUIContent("+", "New tab (Ctrl+T)\nRight-click: Quick Access");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarButton, GUILayout.Width(26));
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1 && rect.Contains(e.mousePosition))
            {
                e.Use();
                ShowQuickAccessMenu(rect);
                return;
            }
            if (GUI.Button(rect, content, EditorStyles.toolbarButton))
                OpenInNewTab(null);
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
        void DrawTabListButton()
        {
            var content = new GUIContent("▾", "All tabs / workspaces");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarButton, GUILayout.Width(20));
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

        /// <summary>Tiny name prompt for "Save Tabs As..." — Unity has no built-in text dialog.</summary>
        class WorkspaceNamePopup : PopupWindowContent
        {
            internal TabstepProjectWindow _owner;
            string _name = "";

            public override Vector2 GetWindowSize() => new Vector2(240, 58);

            public override void OnGUI(Rect rect)
            {
                EditorGUILayout.LabelField("Save tabs as workspace", EditorStyles.boldLabel);
                GUI.SetNextControlName("Tabstep.WorkspaceName");
                _name = EditorGUILayout.TextField(_name);
                EditorGUI.FocusTextInControl("Tabstep.WorkspaceName");
                bool submit = Event.current.type == EventType.KeyDown &&
                              (Event.current.keyCode == KeyCode.Return ||
                               Event.current.keyCode == KeyCode.KeypadEnter);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_name)))
                    if (GUILayout.Button("Save") || (submit && !string.IsNullOrWhiteSpace(_name)))
                    {
                        _owner.SaveWorkspace(_name);
                        editorWindow.Close();
                    }
            }
        }

        /// <summary>
        /// Appears at the right of the tab bar only while assets are being dragged:
        /// dropping parks them on the shelf for a later hand-off instead of moving them.
        /// </summary>
        void DrawShelfDropZone()
        {
            if (!_dragZoneVisible) return;
            var content = new GUIContent("▼ Shelf", "Drop here to park on the shelf");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarButton, GUILayout.Width(64));
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
            if (Selection.objects.Length > 0)
                menu.AddItem(new GUIContent("Send Selection to Shelf"), false,
                    () => TabstepShelfWindow.ShowNear(this).AddObjects(Selection.objects));
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

        /// <summary>Active tab's folder — the shelf hands assets off here.</summary>
        internal string ActiveFolderPath => _session.ActiveTab?.CurrentPath;

        /// <summary>Moves assets into the active tab's folder (used by the shelf's "→ Tab").</summary>
        internal int MoveAssetsToActiveFolder(List<string> paths)
        {
            return MoveAssetsTo(ActiveFolderPath, paths);
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
                DrawSearchChips();
                GUILayout.Space(2);
            }

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

        /// <summary>One-click t: filters next to the search field (configurable in preferences).</summary>
        void DrawSearchChips()
        {
            var chips = TabstepSettings.SearchChips;
            if (string.IsNullOrWhiteSpace(chips)) return;
            foreach (var raw in chips.Split(','))
            {
                var chip = raw.Trim();
                if (chip.Length == 0) continue;
                string token = "t:" + chip;
                bool on = HasSearchToken(_searchText, token);
                bool now = GUILayout.Toggle(on, new GUIContent(chip, "Toggle the " + token + " filter"),
                    EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
                if (now == on) continue;
                var text = ToggleSearchToken(_host.GetSearchText() ?? _searchText, token);
                _searchText = text;
                _host.SetSearch(text);
                _lastAppliedSearch = _host.GetSearchText() ?? text;
                if (_session.ActiveTab != null)
                    _session.ActiveTab.SearchText = text;
            }
        }

        /// <summary>Whitespace-token containment, case-insensitive ("t:Prefab" in "boss t:Prefab").</summary>
        internal static bool HasSearchToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var part in text.Split(' '))
                if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Adds the token to the search text, or removes it when already present.</summary>
        internal static string ToggleSearchToken(string text, string token)
        {
            text ??= "";
            if (!HasSearchToken(text, token))
                return (text + " " + token).Trim();
            var parts = new List<string>();
            foreach (var part in text.Split(' '))
                if (part.Length > 0 && !part.Equals(token, StringComparison.OrdinalIgnoreCase))
                    parts.Add(part);
            return string.Join(" ", parts);
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

        // ---- status bar ----------------------------------------------------------

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
