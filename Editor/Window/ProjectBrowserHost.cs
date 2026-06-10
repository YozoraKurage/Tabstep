using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Hosts an instance of Unity's internal ProjectBrowser (the stock Project window)
    /// inside another EditorWindow. ProjectBrowser is internal, so instead of inheriting
    /// it directly this wrapper instantiates it and delegates OnGUI via reflection —
    /// the embedded pane is the real Project window. Its toolbar and path header can be
    /// folded away into the host's own bar (see <see cref="ProjectBrowserPatcher"/>).
    ///
    /// Everything reflective is null-guarded: if a future Unity version renames a member,
    /// <see cref="IsAvailable"/> turns false and the window shows a fallback message
    /// instead of throwing.
    /// </summary>
    class ProjectBrowserHost : IDisposable
    {
        internal static readonly Type BrowserType =
            typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ProjectBrowser");

        static readonly FieldInfo ParentField =
            typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo PosField =
            typeof(EditorWindow).GetField("m_Pos", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly MethodInfo OnGUIMethod = FindMethod("OnGUI", 0);

        // Builds the browser's internals (search filter, drop lists, tree views).
        // Older Unity versions call it InitIfNeeded; 2022 renamed it to Init (guarded
        // by Initialized()). It must run before SetTwoColumns / SetFolderSelection:
        // both dereference structures it creates (m_AssetLabels, m_FolderTree...) and
        // throw NullReferenceException on a fresh, never-painted browser otherwise.
        static readonly MethodInfo InitMethod = FindMethod("InitIfNeeded", 0) ?? FindMethod("Init", 0);

        static readonly MethodInfo GetActiveFolderPathMethod = FindMethod("GetActiveFolderPath", 0);
        static readonly MethodInfo SetTwoColumnsMethod = FindMethod("SetTwoColumns", 0);

        // Optional members for hiding the browser's own "Assets > ..." path header
        // (Tabstep's address bar replaces it). Missing members just leave it visible.
        static readonly FieldInfo ListHeaderRectField =
            BrowserType?.GetField("m_ListHeaderRect", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo ListAreaRectField =
            BrowserType?.GetField("m_ListAreaRect", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo SearchFilterField =
            BrowserType?.GetField("m_SearchFilter", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly MethodInfo IsSearchingMethod =
            SearchFilterField?.FieldType.GetMethod("IsSearching", BindingFlags.Instance | BindingFlags.Public);

        // Optional members bridging the browser's search into the host's own toolbar
        // (used while ProjectBrowserPatcher hides the stock toolbar).
        static readonly MethodInfo SetSearchMethod = BrowserType?
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m =>
            {
                if (m.Name != "SetSearch") return false;
                var p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(string);
            });

        static readonly PropertyInfo SearchTextProperty =
            BrowserType?.GetProperty("searchText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static readonly MethodInfo SetAsLastInteractedMethod = FindMethod("SetAsLastInteractedProjectBrowser", 0);

        // internal void SetFolderSelection(int[] selectedInstanceIDs, bool revealSelectionAndFrameLastSelected)
        static readonly MethodInfo SetFolderSelectionMethod = BrowserType?
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m =>
            {
                if (m.Name != "SetFolderSelection") return false;
                var p = m.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(int[]) && p[1].ParameterType == typeof(bool);
            });

        static MethodInfo FindMethod(string name, int paramCount)
        {
            return BrowserType?
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == paramCount);
        }

        /// <summary>False when the internal API this relies on is missing in the running Unity version.</summary>
        public static bool IsAvailable =>
            BrowserType != null && PosField != null && ParentField != null &&
            OnGUIMethod != null && GetActiveFolderPathMethod != null && SetFolderSelectionMethod != null;

        readonly EditorWindow _owner;
        readonly HashSet<string> _warnedMethods = new HashSet<string>();
        EditorWindow _browser;

        public ProjectBrowserHost(EditorWindow owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_browser != null)
            {
                ProjectBrowserPatcher.Unregister(_browser);
                // Detach the borrowed host view before destruction so teardown code
                // never mistakes the browser for a real docked window.
                ParentField?.SetValue(_browser, null);
                Object.DestroyImmediate(_browser);
            }
            _browser = null;
        }

        bool EnsureBrowser()
        {
            if (_browser != null) return true;
            if (!IsAvailable) return false;
            _browser = ScriptableObject.CreateInstance(BrowserType) as EditorWindow;
            if (_browser == null) return false;
            // Never saved into the layout file — the host window owns and recreates it.
            _browser.hideFlags = HideFlags.HideAndDontSave;
            // Opt this instance into the compact-layout Harmony patches (no-op without Harmony).
            ProjectBrowserPatcher.Register(_browser);
            AttachToOwner();
            // Init sizes its panes from `position`, so swap the placeholder rect a
            // fresh instance carries for the owner's size before running it.
            var size = _owner.position.size;
            PosField.SetValue(_browser, new Rect(0, 0, Mathf.Max(size.x, 200f), Mathf.Max(size.y, 100f)));
            Invoke(InitMethod);
            // Tabs target folders, which only the two-column mode can display directly.
            Invoke(SetTwoColumnsMethod);
            return true;
        }

        /// <summary>
        /// The browser repaints and resolves focus through m_Parent (its HostView). It is
        /// never shown as a real window, so it borrows the host's. Re-attached every frame
        /// because docking/undocking swaps the owner's parent.
        /// </summary>
        void AttachToOwner()
        {
            var parent = ParentField.GetValue(_owner);
            if (parent != null && !ReferenceEquals(ParentField.GetValue(_browser), parent))
                ParentField.SetValue(_browser, parent);
        }

        object Invoke(MethodInfo method, params object[] args)
        {
            if (method == null || _browser == null) return null;
            try
            {
                return method.Invoke(_browser, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is ExitGUIException)
            {
                // GUIUtility.ExitGUI() inside the browser (drag start, object selector...)
                // must keep flowing as ExitGUIException for IMGUI to unwind normally.
                throw e.InnerException;
            }
            catch (Exception e)
            {
                // Warn once per member — OnGUI runs every frame and must not spam the console.
                if (_warnedMethods.Add(method.Name))
                    Debug.LogWarning(
                        $"[Tabstep] ProjectBrowser call '{method.Name}' failed: {e.InnerException ?? e}");
                return null;
            }
        }

        /// <summary>
        /// Draws the embedded Project browser into <paramref name="rect"/> (window coordinates).
        /// With <paramref name="hidePathHeader"/> the browser's own chrome is reduced: when the
        /// Harmony patches are active (<see cref="ProjectBrowserPatcher"/>) the toolbar and the
        /// "Assets &gt; ..." header are gone entirely and their rows reclaimed. Otherwise this
        /// falls back to neutralizing the header in place: its clicks are swallowed before the
        /// browser sees them and its crumbs are painted over with the bar background — without
        /// patching there is no seam inside the browser's single OnGUI to reclaim the height.
        /// The header area doubles as the search header while searching, which stays untouched.
        /// </summary>
        public void OnGUI(Rect rect, bool hidePathHeader = false)
        {
            if (!EnsureBrowser()) return;
            AttachToOwner();
            // ProjectBrowser lays itself out against `position`; only the size matters here
            // because rendering happens inside a GUI area at the rect's origin.
            PosField.SetValue(_browser, new Rect(0, 0, rect.width, rect.height));
            GUILayout.BeginArea(rect);
            try
            {
                // With the Harmony patches active the header is zero-height while browsing,
                // so the swallow/cover fallback below naturally no-ops. Without SetTwoColumns
                // the browser fell back to one-column mode, where the header strip is
                // occupied by the asset tree instead — leave it alone.
                bool hideHeader = hidePathHeader && SetTwoColumnsMethod != null && !IsSearching(_browser);
                if (hideHeader)
                    SwallowPathHeaderEvents(PathHeaderRect()); // last frame's rect; stable between frames
                Invoke(OnGUIMethod);
                if (hideHeader)
                    CoverPathHeader(PathHeaderRect());
                if (hidePathHeader && ProjectBrowserPatcher.Active)
                    DrawSplitterTopGap();
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        /// <summary>
        /// With the toolbar row reclaimed, the browser still draws the tree/list splitter
        /// line from the old toolbar offset down — fill in the missing topmost stretch.
        /// </summary>
        void DrawSplitterTopGap()
        {
            if (Event.current.type != EventType.Repaint || ListAreaRectField == null) return;
            var listArea = (Rect)ListAreaRectField.GetValue(_browser);
            if (listArea.width <= 0f) return;
            EditorGUI.DrawRect(new Rect(listArea.x, 0, 1, EditorStyles.toolbar.fixedHeight),
                EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.6f, 0.6f, 0.6f));
        }

        // ---- built-in path header ("Assets > ...") -----------------------------

        static GUIStyle _headerCoverStyle;

        /// <summary>Header rect in browser-local coordinates (== area-local), zero when unknown.</summary>
        Rect PathHeaderRect()
        {
            if (_browser == null || ListHeaderRectField == null) return Rect.zero;
            return (Rect)ListHeaderRectField.GetValue(_browser);
        }

        /// <summary>True while <paramref name="browser"/> shows search results instead of a folder.</summary>
        internal static bool IsSearching(object browser)
        {
            if (browser == null || SearchFilterField == null || IsSearchingMethod == null) return false;
            try
            {
                var filter = SearchFilterField.GetValue(browser);
                return filter != null && (bool)IsSearchingMethod.Invoke(filter, null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Eats clicks aimed at the path header so its (visually hidden) crumbs stay inert.
        /// MouseUp passes through: the crumbs never become the hot control without the down.
        /// </summary>
        static void SwallowPathHeaderEvents(Rect headerRect)
        {
            var e = Event.current;
            if (headerRect.height <= 0f || !headerRect.Contains(e.mousePosition)) return;
            if (e.type == EventType.MouseDown || e.type == EventType.ContextClick)
                e.Use();
        }

        /// <summary>
        /// Repaints the header's own bar background over its crumbs, leaving the bar empty
        /// for the owner to draw into. Left edge inset keeps the tree/list splitter intact.
        /// </summary>
        static void CoverPathHeader(Rect headerRect)
        {
            if (Event.current.type != EventType.Repaint || headerRect.height <= 0f) return;
            headerRect.xMin += 2;
            _headerCoverStyle ??= GUI.skin.FindStyle("ProjectBrowserTopBarBg");
            if (_headerCoverStyle != null)
                _headerCoverStyle.Draw(headerRect, GUIContent.none, false, false, false, false);
            else
                EditorGUI.DrawRect(headerRect, EditorGUIUtility.isProSkin
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.76f, 0.76f, 0.76f));
        }

        // ---- search bridge (the host toolbar owns the field when patched) -------

        /// <summary>The browser's current search text, or null when unknown.</summary>
        public string GetSearchText()
        {
            if (_browser == null || SearchTextProperty == null) return null;
            try
            {
                return SearchTextProperty.GetValue(_browser) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Applies search text to the embedded browser (same syntax as the stock field).</summary>
        public void SetSearch(string text)
        {
            if (!EnsureBrowser()) return;
            Invoke(SetSearchMethod, text ?? "");
        }

        /// <summary>
        /// Marks the embedded browser as the "last interacted" Project browser, so
        /// Assets/Create menu items target its current folder.
        /// </summary>
        public void MarkAsLastInteracted()
        {
            if (_browser == null) return;
            Invoke(SetAsLastInteractedMethod);
        }

        /// <summary>
        /// The asset list (right column / search results) rect in host-content
        /// coordinates, zero when unknown. Lets the host tell list clicks apart from
        /// folder-tree clicks.
        /// </summary>
        public Rect GetListAreaRect()
        {
            if (_browser == null || ListAreaRectField == null) return Rect.zero;
            try
            {
                return (Rect)ListAreaRectField.GetValue(_browser);
            }
            catch
            {
                return Rect.zero;
            }
        }

        /// <summary>Folder currently shown by the embedded browser, or null when unknown.</summary>
        public string GetActiveFolderPath()
        {
            if (_browser == null) return null;
            return ProjectPaths.Normalize(Invoke(GetActiveFolderPathMethod) as string);
        }

        /// <summary>Points the embedded browser at a folder. False when the folder no longer exists.</summary>
        public bool ShowFolder(string projectPath)
        {
            if (!EnsureBrowser()) return false;
            if (string.IsNullOrEmpty(projectPath) || !AssetDatabase.IsValidFolder(projectPath)) return false;
            var folder = AssetDatabase.LoadAssetAtPath<Object>(projectPath);
            if (folder == null) return false;
            Invoke(InitMethod);
            Invoke(SetFolderSelectionMethod, new[] { folder.GetInstanceID() }, false);
            return true;
        }
    }
}
