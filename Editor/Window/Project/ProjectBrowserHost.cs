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
    internal sealed class ProjectBrowserHost : IDisposable
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

        // internal static int[] GetTreeViewFolderSelection(bool forceUseTreeViewSelection)
        // — what Unity's own delete/rename commands use to act on the folder tree pane.
        static readonly MethodInfo GetTreeViewFolderSelectionMethod = BrowserType?
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetTreeViewFolderSelection" && m.GetParameters().Length == 1);

        // The asset list and its repaint hook (Action repaintCallback) — used to route
        // the hosted browser's repaint requests to the host window.
        static readonly FieldInfo ListAreaField =
            BrowserType?.GetField("m_ListArea", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly PropertyInfo ListAreaRepaintCallbackProperty =
            ListAreaField?.FieldType.GetProperty("repaintCallback",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // internal void FrameObject(int instanceID, bool ping) — scrolls the asset
        // list/tree so the object is visible. Optional: missing just skips framing.
        static readonly MethodInfo FrameObjectMethod = FindMethod("FrameObject", 2);

        // internal void SetFolderSelection(int[] selectedInstanceIDs, bool revealSelectionAndFrameLastSelected)
        static readonly MethodInfo SetFolderSelectionMethod = BrowserType?
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m =>
            {
                if (m.Name != "SetFolderSelection") return false;
                var p = m.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(int[]) && p[1].ParameterType == typeof(bool);
            });

        // public void InitSelection(int[] selectedInstanceIDs) on ObjectListArea — the
        // list pane's selection store. Selection.assetGUIDs / Export Package / Find
        // References query this on the last-interacted browser, so it must mirror the
        // user's column-view selection or those features see a stale list.
        static readonly MethodInfo ListAreaInitSelectionMethod = ListAreaField?.FieldType
            .GetMethod("InitSelection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // CreateAssetUtility access — the fallback path when our Harmony prefix on
        // BeginPreimportedNameEditing did not install (no Harmony, or a future Unity
        // renamed the entry point). We poll the browser's in-flight create state out
        // of here each Layout pass and reset it so the (invisible) browser overlay
        // does not also run alongside the column-view rename.
        static readonly MethodInfo ListAreaGetCreateAssetUtilityMethod = ListAreaField?.FieldType
            .GetMethod("GetCreateAssetUtility", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type CreateAssetUtilityType = ListAreaGetCreateAssetUtilityMethod?.ReturnType;
        static readonly MethodInfo CreateAssetUtilityIsCreatingMethod = CreateAssetUtilityType?
            .GetMethod("IsCreatingNewAsset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly PropertyInfo CreateAssetUtilityInstanceIDProp = CreateAssetUtilityType?
            .GetProperty("instanceID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly PropertyInfo CreateAssetUtilityEndActionProp = CreateAssetUtilityType?
            .GetProperty("endAction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly PropertyInfo CreateAssetUtilityIconProp = CreateAssetUtilityType?
            .GetProperty("icon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        // The full asset path being named (e.g. "Assets/Foo/NewBehaviourScript.cs").
        // Unity calls the field m_Path but exposes it as `folder` for historical reasons.
        static readonly PropertyInfo CreateAssetUtilityFolderProp = CreateAssetUtilityType?
            .GetProperty("folder", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly PropertyInfo CreateAssetUtilityResourceFileProp = CreateAssetUtilityType?
            .GetProperty("resourceFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo CreateAssetUtilityResetMethod = CreateAssetUtilityType?
            .GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Direct field access for the CreateAssetUtility state. The properties are the
        // documented surface, but a single failed Reset() reflection lookup in any
        // Unity build leaves the utility populated forever and our polling re-feeds
        // the same request to the column view on every tick. Clearing the fields
        // by hand is the belt to Reset's suspenders.
        static readonly FieldInfo CreateAssetUtilityInstanceIDField = CreateAssetUtilityType?
            .GetField("m_InstanceID", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CreateAssetUtilityPathField = CreateAssetUtilityType?
            .GetField("m_Path", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CreateAssetUtilityIconField = CreateAssetUtilityType?
            .GetField("m_Icon", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CreateAssetUtilityResourceFileField = CreateAssetUtilityType?
            .GetField("m_ResourceFile", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CreateAssetUtilityEndActionField = CreateAssetUtilityType?
            .GetField("m_EndAction", BindingFlags.Instance | BindingFlags.NonPublic);

        // public RenameOverlay GetRenameOverlay() — on ObjectListArea. Needed so the
        // browser's now-orphan inline rename overlay (it kept renaming after we Reset
        // the create utility) gets dismissed; otherwise its hidden text field steals
        // keyboard focus from the column view's overlay.
        static readonly MethodInfo ListAreaGetRenameOverlayMethod = ListAreaField?.FieldType
            .GetMethod("GetRenameOverlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type RenameOverlayType = ListAreaGetRenameOverlayMethod?.ReturnType;
        static readonly MethodInfo RenameOverlayEndRenameMethod = RenameOverlayType?
            .GetMethod("EndRename", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null);

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
        // Folder the browser showed when the owner last painted — detects the browser
        // navigating itself (folder double-click, tree-pane click).
        string _paintedFolder;

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
            // Drop any captured Assets/Create/... request the owner never got around
            // to consuming, so it does not leak into the next browser instance.
            AssetCreationBridge.Discard(_owner);
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
            ProjectBrowserPatcher.Register(_browser, _owner);
            AttachToOwner();
            // Init sizes its panes from `position`, so swap the placeholder rect a
            // fresh instance carries for the owner's size before running it.
            var size = _owner.position.size;
            PosField.SetValue(_browser, new Rect(0, 0, Mathf.Max(size.x, 200f), Mathf.Max(size.y, 100f)));
            Invoke(InitMethod);
            HookListAreaRepaint();
            // Tabs target folders, which only the two-column mode can display directly.
            Invoke(SetTwoColumnsMethod);
            return true;
        }

        /// <summary>
        /// Routes the asset list's repaint requests to the host window. They normally
        /// flow into the browser's own Repaint(), but EditorWindow.Repaint only reaches
        /// the parent view for that view's actualView — which the embedded browser never
        /// is — so they all get dropped: thumbnails streaming in after a folder opens
        /// would only show on the next incidental event (a mouse move). The list is
        /// created once per browser instance, so hooking once here is enough.
        /// </summary>
        void HookListAreaRepaint()
        {
            if (ListAreaField == null || ListAreaRepaintCallbackProperty == null) return;
            try
            {
                var listArea = ListAreaField.GetValue(_browser);
                if (listArea == null) return;
                var current = ListAreaRepaintCallbackProperty.GetValue(listArea) as Action;
                ListAreaRepaintCallbackProperty.SetValue(listArea, current + (Action)RepaintOwner);
            }
            catch (Exception e)
            {
                // Cosmetic only — RepaintOwnerOnSelfNavigation still covers folder changes.
                if (_warnedMethods.Add("repaintCallback"))
                    Debug.LogWarning($"[Tabstep] Could not hook the list repaint: {e}");
            }
        }

        void RepaintOwner()
        {
            if (_owner != null) _owner.Repaint();
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
                RepaintOwnerOnSelfNavigation();
            }
        }

        /// <summary>
        /// Repaints the owner when the browser navigated itself during this pass. The
        /// browser requests a repaint for that, but its EditorWindow.Repaint is a silent
        /// no-op while hosted (see <see cref="HookListAreaRepaint"/>) — without this the
        /// newly opened folder would only draw on the next incidental event. Runs in the
        /// finally above because opening from the asset list ends in GUIUtility.ExitGUI.
        /// </summary>
        void RepaintOwnerOnSelfNavigation()
        {
            var folder = GetActiveFolderPath();
            if (folder == _paintedFolder) return;
            _paintedFolder = folder;
            RepaintOwner();
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

        /// <summary>True while the embedded browser shows search results instead of a folder.</summary>
        public bool IsSearching()
        {
            return IsSearching(_browser);
        }

        /// <summary>Scrolls the embedded browser so the object is visible (no ping).</summary>
        public void FrameObject(int instanceID)
        {
            if (_browser == null) return;
            Invoke(FrameObjectMethod, instanceID, false);
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

        /// <summary>
        /// The folders selected in the folder tree pane (left column) of the last
        /// interacted Project browser — stock or embedded. That tree keeps its
        /// selection out of the global Selection, so features acting on "the selected
        /// objects" would miss it otherwise. Non-empty only while the tree has keyboard
        /// focus; the Packages root is a virtual item without an Object and is skipped.
        /// </summary>
        internal static Object[] GetFolderTreeSelection()
        {
            if (GetTreeViewFolderSelectionMethod == null) return Array.Empty<Object>();
            try
            {
                var ids = GetTreeViewFolderSelectionMethod.Invoke(null, new object[] { false }) as int[];
                if (ids == null || ids.Length == 0) return Array.Empty<Object>();
                var objects = new List<Object>(ids.Length);
                foreach (var id in ids)
                {
                    var obj = EditorUtility.InstanceIDToObject(id);
                    if (obj != null) objects.Add(obj);
                }
                return objects.ToArray();
            }
            catch
            {
                return Array.Empty<Object>();
            }
        }

        /// <summary>Folder currently shown by the embedded browser, or null when unknown.</summary>
        public string GetActiveFolderPath()
        {
            if (_browser == null) return null;
            return ProjectPaths.Normalize(Invoke(GetActiveFolderPathMethod) as string);
        }

        /// <summary>
        /// Mirrors a list of instance ids into the embedded browser's list-area
        /// selection. <see cref="Selection"/>.assetGUIDs and features built on it
        /// (Assets/Export Package..., Find References in Project, ...) read this on
        /// the last-interacted ProjectBrowser, so the column view must push its own
        /// selection here or those features see whatever was selected before.
        /// </summary>
        public void SyncListAreaSelection(int[] selectedInstanceIDs)
        {
            if (_browser == null || ListAreaField == null || ListAreaInitSelectionMethod == null) return;
            try
            {
                var listArea = ListAreaField.GetValue(_browser);
                if (listArea == null) return;
                ListAreaInitSelectionMethod.Invoke(listArea, new object[] { selectedInstanceIDs ?? Array.Empty<int>() });
            }
            catch (Exception e)
            {
                if (_warnedMethods.Add("InitSelection"))
                    Debug.LogWarning($"[Tabstep] Could not sync the embedded browser's selection: {e}");
            }
        }

        /// <summary>
        /// Tears down any in-flight inline-rename create the embedded browser is
        /// holding — resets the create utility and ends the rename overlay so the
        /// post-handle check that calls EndAction.Cancelled does not fire and the
        /// overlay's hidden text field stops grabbing focus. Safe to call when no
        /// create is in flight.
        /// </summary>
        public void ResetBrowserCreate()
        {
            if (_browser == null || ListAreaField == null) return;
            if (ListAreaGetCreateAssetUtilityMethod == null) return;
            try
            {
                var listArea = ListAreaField.GetValue(_browser);
                if (listArea == null) return;
                var utility = ListAreaGetCreateAssetUtilityMethod.Invoke(listArea, null);
                if (utility != null)
                {
                    CreateAssetUtilityResetMethod?.Invoke(utility, null);
                    // Wipe the fields directly too — the only thing the polling check
                    // gates on is m_InstanceID != 0 and a non-empty m_Path, so the
                    // create utility must look unambiguously empty after this call.
                    CreateAssetUtilityInstanceIDField?.SetValue(utility, 0);
                    CreateAssetUtilityPathField?.SetValue(utility, string.Empty);
                    CreateAssetUtilityIconField?.SetValue(utility, null);
                    CreateAssetUtilityResourceFileField?.SetValue(utility, string.Empty);
                    CreateAssetUtilityEndActionField?.SetValue(utility, null);
                }
                if (ListAreaGetRenameOverlayMethod != null && RenameOverlayEndRenameMethod != null)
                {
                    var overlay = ListAreaGetRenameOverlayMethod.Invoke(listArea, null);
                    if (overlay != null) RenameOverlayEndRenameMethod.Invoke(overlay, new object[] { false });
                }
            }
            catch (Exception e)
            {
                if (_warnedMethods.Add("ResetBrowserCreate"))
                    Debug.LogWarning($"[Tabstep] Could not reset CreateAssetUtility: {e}");
            }
        }

        /// <summary>
        /// Reads any inline-rename create the embedded browser started (the path used
        /// when our Harmony prefix on BeginPreimportedNameEditing did not install),
        /// resets the browser's create utility and ends its rename overlay so the
        /// column view can drive the rename. Returns null when nothing is pending.
        /// </summary>
        public AssetCreationBridge.Request TakeBrowserCreateInProgress()
        {
            if (_browser == null || ListAreaField == null) return null;
            if (ListAreaGetCreateAssetUtilityMethod == null) return null;
            try
            {
                var listArea = ListAreaField.GetValue(_browser);
                if (listArea == null) return null;
                var utility = ListAreaGetCreateAssetUtilityMethod.Invoke(listArea, null);
                if (utility == null) return null;

                // Read straight from the backing fields — properties have the same
                // information and slightly different names across Unity versions
                // ("folder" returning m_Path is the awkward example) so this path is
                // both more robust and easier to keep clearing in lock-step.
                int instanceID = CreateAssetUtilityInstanceIDField != null
                    ? (int)CreateAssetUtilityInstanceIDField.GetValue(utility)
                    : (CreateAssetUtilityInstanceIDProp != null
                        ? (int)CreateAssetUtilityInstanceIDProp.GetValue(utility) : 0);
                if (instanceID == 0) return null; // nothing in flight

                string pathName = (CreateAssetUtilityPathField?.GetValue(utility) as string)
                    ?? (CreateAssetUtilityFolderProp?.GetValue(utility) as string);
                if (string.IsNullOrEmpty(pathName)) return null;

                var request = new AssetCreationBridge.Request
                {
                    InstanceID = instanceID,
                    PathName = pathName,
                    Icon = (CreateAssetUtilityIconField?.GetValue(utility) as Texture2D)
                        ?? (CreateAssetUtilityIconProp?.GetValue(utility) as Texture2D),
                    ResourceFile = (CreateAssetUtilityResourceFileField?.GetValue(utility) as string)
                        ?? (CreateAssetUtilityResourceFileProp?.GetValue(utility) as string),
                    EndAction = (CreateAssetUtilityEndActionField?.GetValue(utility)
                        as UnityEditor.ProjectWindowCallback.EndNameEditAction)
                        ?? (CreateAssetUtilityEndActionProp?.GetValue(utility)
                            as UnityEditor.ProjectWindowCallback.EndNameEditAction),
                };

                // Clear the browser's hold on the create — the post-handle check
                // gates on IsCreatingNewAsset(), which reads m_InstanceID. Reset()
                // first (Unity's own zeroing path), then wipe each field by hand so
                // a missing Reset method binding cannot leave a partially-populated
                // utility that the next poll would re-feed to the column view.
                CreateAssetUtilityResetMethod?.Invoke(utility, null);
                CreateAssetUtilityInstanceIDField?.SetValue(utility, 0);
                CreateAssetUtilityPathField?.SetValue(utility, string.Empty);
                CreateAssetUtilityIconField?.SetValue(utility, null);
                CreateAssetUtilityResourceFileField?.SetValue(utility, string.Empty);
                CreateAssetUtilityEndActionField?.SetValue(utility, null);
                // Dismiss the now-orphan rename overlay so its hidden text field stops
                // grabbing focus away from the column view's overlay.
                if (ListAreaGetRenameOverlayMethod != null && RenameOverlayEndRenameMethod != null)
                {
                    var overlay = ListAreaGetRenameOverlayMethod.Invoke(listArea, null);
                    if (overlay != null) RenameOverlayEndRenameMethod.Invoke(overlay, new object[] { false });
                }
                return request;
            }
            catch (Exception e)
            {
                if (_warnedMethods.Add("CreateAssetUtility"))
                    Debug.LogWarning($"[Tabstep] Could not read CreateAssetUtility: {e}");
                return null;
            }
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
