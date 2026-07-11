using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Harmony patches that fold the hosted Project browser's chrome into Tabstep's
    /// navigation bar: the browser's toolbar is skipped (its create button and search
    /// field live in the bar instead), the "Assets &gt; ..." path header collapses to
    /// zero height, and the content rects shift up to reclaim both rows. While
    /// searching the header keeps its height — it shows the search scope there.
    ///
    /// Only browsers registered by <see cref="ProjectBrowserHost"/> are affected, and
    /// only while the navigation bar is enabled; stock Project windows stay untouched.
    /// Harmony itself is optional and loaded by reflection (the VRChat SDK ships
    /// 0Harmony.dll; the README covers standalone installation). Without it, or when
    /// any patch target is missing, <see cref="Active"/> stays false and the host
    /// falls back to cover-painting the path header.
    /// </summary>
    static class ProjectBrowserPatcher
    {
        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static readonly FieldInfo TreeViewRectField =
            ProjectBrowserHost.BrowserType?.GetField("m_TreeViewRect", InstanceFlags);

        static readonly FieldInfo ListAreaRectField =
            ProjectBrowserHost.BrowserType?.GetField("m_ListAreaRect", InstanceFlags);

        static readonly FieldInfo ListHeaderRectField =
            ProjectBrowserHost.BrowserType?.GetField("m_ListHeaderRect", InstanceFlags);

        static readonly HashSet<object> HostedBrowsers = new HashSet<object>();
        // browser -> owning Tabstep window. Lets the BeginPreimportedNameEditing
        // patch find which window's column view should drive the new asset rename.
        static readonly Dictionary<object, EditorWindow> BrowserOwners =
            new Dictionary<object, EditorWindow>();
        static bool _initialized;
        static bool _active;

        // Non-zero while a Tabstep-internal caller wants the browser's real
        // GetActiveFolderPath — used by SyncWithBrowser and the host's own
        // navigation detector to read the folder the tree pane clicked into,
        // instead of the tab-path override Unity's Create code needs. Counter,
        // not bool, so nested bypass scopes unwind cleanly.
        [ThreadStatic] static int _bypassDepth;

        /// <summary>True when Harmony was found and every patch applied.</summary>
        public static bool Active
        {
            get
            {
                Initialize();
                return _active;
            }
        }

        public static void Register(object browser, EditorWindow owner)
        {
            Initialize();
            if (browser == null) return;
            HostedBrowsers.Add(browser);
            if (owner != null) BrowserOwners[browser] = owner;
        }

        public static void Unregister(object browser)
        {
            if (browser == null) return;
            HostedBrowsers.Remove(browser);
            BrowserOwners.Remove(browser);
        }

        /// <summary>
        /// Ask the <see cref="GetActiveFolderPathPrefix"/> to fall through to Unity's
        /// original implementation for the duration of the returned scope. Used by
        /// Tabstep's own callers (browser-navigation detector, sync loop) so they
        /// read the real folder the browser shows, not the tab-path override we
        /// hand back to <c>ProjectWindowUtil.Create*</c>. Safe to nest.
        /// </summary>
        public static IDisposable BypassGetActiveFolderPath() => new BypassScope();

        sealed class BypassScope : IDisposable
        {
            public BypassScope() { _bypassDepth++; }
            public void Dispose() { if (_bypassDepth > 0) _bypassDepth--; }
        }

        static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                _active = TryPatch();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Harmony patching failed, using the fallback layout: {e}");
                _active = false;
            }
        }

        static bool TryPatch()
        {
            var browser = ProjectBrowserHost.BrowserType;
            var harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony");
            var harmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony");
            if (browser == null || harmonyType == null || harmonyMethodType == null) return false;
            if (TreeViewRectField == null || ListAreaRectField == null || ListHeaderRectField == null) return false;

            var topToolbar = browser.GetMethod("TopToolbar", InstanceFlags);
            var listHeaderHeight = browser.GetMethod("GetListHeaderHeight", InstanceFlags);
            var calculateRects = browser.GetMethod("CalculateRects", InstanceFlags);
            // All-or-nothing: applying only some of the patches would, say, remove the
            // toolbar while still reserving its row.
            if (topToolbar == null || listHeaderHeight == null || calculateRects == null) return false;

            // Harmony.Patch(original, prefix, postfix, ...) — arguments are bound by
            // parameter name so optional parameters added in newer Harmony versions
            // don't break the call.
            var patch = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return m.Name == "Patch" && p.Length >= 3 && p[0].ParameterType == typeof(MethodBase);
                });
            if (patch == null) return false;
            var harmony = Activator.CreateInstance(harmonyType, "com.yozolab.tabstep");

            const BindingFlags Self = BindingFlags.Static | BindingFlags.NonPublic;
            Apply(patch, harmony, harmonyMethodType, topToolbar,
                prefix: typeof(ProjectBrowserPatcher).GetMethod(nameof(TopToolbarPrefix), Self));
            Apply(patch, harmony, harmonyMethodType, listHeaderHeight,
                prefix: typeof(ProjectBrowserPatcher).GetMethod(nameof(GetListHeaderHeightPrefix), Self));
            Apply(patch, harmony, harmonyMethodType, calculateRects,
                postfix: typeof(ProjectBrowserPatcher).GetMethod(nameof(CalculateRectsPostfix), Self));

            // Best-effort, ungated: intercept "Assets/Create/..." so the new asset is named
            // through the column view (its overlay covers the browser's own rename field).
            // A failure here must not sink the layout patches above — without this patch
            // the column view only catches creations that finalise on disk (folders), not
            // pre-imported ones (scripts) whose name has to be entered before they exist.
            try
            {
                var beginRename = browser.GetMethod("BeginPreimportedNameEditing", InstanceFlags);
                var beginRenamePrefix = typeof(ProjectBrowserPatcher).GetMethod(
                    nameof(BeginPreimportedNameEditingPrefix), Self);
                if (beginRename != null && beginRenamePrefix != null)
                    Apply(patch, harmony, harmonyMethodType, beginRename, prefix: beginRenamePrefix);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not hook BeginPreimportedNameEditing: {e}");
            }

            // Pin GetActiveFolderPath on the hosted browser to the active tab's folder so
            // ProjectWindowUtil.Create* lands the new asset where the column view is
            // actually looking — no matter what Selection.activeObject happens to be.
            // Without this the destination drifted to whichever folder the last-selected
            // asset belonged to, and the column view never saw the create that followed.
            try
            {
                var getActiveFolderPath = browser.GetMethod("GetActiveFolderPath", InstanceFlags);
                var getActiveFolderPathPrefix = typeof(ProjectBrowserPatcher).GetMethod(
                    nameof(GetActiveFolderPathPrefix), Self);
                if (getActiveFolderPath != null && getActiveFolderPathPrefix != null)
                    Apply(patch, harmony, harmonyMethodType, getActiveFolderPath,
                        prefix: getActiveFolderPathPrefix);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not hook GetActiveFolderPath: {e}");
            }

            // Best-effort, ungated: record asset pings so the type-column view can flash the
            // pinged item (the stock list draws that flash, but it hides behind the column
            // view). A failure here must not sink the layout patches above.
            try
            {
                var pingObject = typeof(EditorGUIUtility).GetMethod("PingObject",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
                var pingPostfix = typeof(ProjectBrowserPatcher).GetMethod(nameof(PingObjectPostfix), Self);
                if (pingObject != null && pingPostfix != null)
                    Apply(patch, harmony, harmonyMethodType, pingObject, postfix: pingPostfix);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not hook PingObject for the column view flash: {e}");
            }
            return true;
        }

        static void Apply(MethodInfo patch, object harmony, Type harmonyMethodType,
            MethodBase original, MethodInfo prefix = null, MethodInfo postfix = null)
        {
            var parameters = patch.GetParameters();
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                switch (parameters[i].Name)
                {
                    case "original":
                        args[i] = original;
                        break;
                    case "prefix" when prefix != null:
                        args[i] = Activator.CreateInstance(harmonyMethodType, prefix);
                        break;
                    case "postfix" when postfix != null:
                        args[i] = Activator.CreateInstance(harmonyMethodType, postfix);
                        break;
                }
            }
            patch.Invoke(harmony, args);
        }

        // ProjectBrowser.BeginPreimportedNameEditing(int, EndNameEditAction, string, Texture2D, string)
        // — Unity's choke point for the "Assets/Create/..." inline rename. While the
        // active tab is in TypeColumns mode the browser's own overlay is invisible
        // (the column view covers the list area), so capture the request for the
        // column view to drive and skip the original. Other tabs (Stock view) and
        // every non-hosted browser run the standard path unchanged.
        static bool BeginPreimportedNameEditingPrefix(
            object __instance,
            int instanceID,
            UnityEditor.ProjectWindowCallback.EndNameEditAction endAction,
            string pathName,
            Texture2D icon,
            string resourceFile)
        {
            if (!BrowserOwners.TryGetValue(__instance, out var owner) || owner == null) return true;
            var window = owner as TabstepProjectWindow;
            if (window == null || !window.ShouldInterceptNewAssetRename()) return true;
            if (string.IsNullOrEmpty(pathName)) return true;

            // Unity's built-in creates hand this method a bare file name ("New
            // Folder", "NewBehaviourScript.cs", ...); the resolution to a full
            // project path lives in CreateAssetUtility.BeginNewAssetCreation —
            // inside the very call this prefix skips. Mirror it here, against the
            // active tab's folder (where the GetActiveFolderPath patch below pins
            // every create). Everything downstream assumes a full path: a bare
            // name fell into the column view's folder-mismatch guard, which
            // silently cancelled the whole create — no phantom, no rename, no asset.
            pathName = pathName.Replace('\\', '/');
            if (!pathName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                !pathName.StartsWith("packages/", StringComparison.OrdinalIgnoreCase))
            {
                string destination = window.ColumnViewCreateDestination();
                if (string.IsNullOrEmpty(destination)) return true; // no tab folder — stock flow
                pathName = AssetDatabase.GenerateUniqueAssetPath(destination + "/" + pathName);
            }
            else
            {
                // Already a full path — unique-ify it exactly like the skipped original.
                pathName = AssetDatabase.GenerateUniqueAssetPath(pathName);
            }

            // Drive the active tab to wherever Unity is about to write the asset
            // (right-click on a subfolder, asset selected in a different folder, ...)
            // so the column-view phantom has the right folder under it. Without this
            // step the original ran and left a phantom in the (covered) browser that
            // only surfaced when the user toggled the column view off.
            window.EnsureTabShowsForCreate(pathName);

            AssetCreationBridge.Submit(owner, new AssetCreationBridge.Request
            {
                InstanceID = instanceID,
                EndAction = endAction,
                PathName = pathName,
                Icon = icon,
                ResourceFile = resourceFile,
            });
            owner.Repaint();
            return false; // the column view runs the rename instead
        }

        // ProjectBrowser.GetActiveFolderPath() — override the result when called on a
        // hosted browser whose column view is active. ProjectWindowUtil.Create* reads
        // this through Selection.assetGUIDs / s_LastInteractedProjectBrowser, and we
        // must hand back the tab's folder so the create lands where the user is
        // looking instead of in whichever folder a stray Selection.activeObject
        // happened to point at.
        static bool GetActiveFolderPathPrefix(object __instance, ref string __result)
        {
            // Tabstep's own callers (SyncWithBrowser, RepaintOwnerOnSelfNavigation)
            // wrap their call in BypassGetActiveFolderPath so they observe tree-pane
            // clicks. Without this fallthrough the override made those clicks
            // invisible and the active tab never followed the tree selection.
            if (_bypassDepth > 0) return true;
            if (!BrowserOwners.TryGetValue(__instance, out var owner) || owner == null) return true;
            var window = owner as TabstepProjectWindow;
            if (window == null) return true;
            var folder = window.ColumnViewCreateDestination();
            if (string.IsNullOrEmpty(folder)) return true;
            __result = folder;
            return false;
        }

        // EditorGUIUtility.PingObject(Object) forwards to the int overload, so hooking this
        // one catches every asset ping. Records it for the type-column view's flash.
        static void PingObjectPostfix(int targetInstanceID)
        {
            try
            {
                PingTracker.Record(targetInstanceID);
                // Kick the column-view windows so the flash starts even if nothing else
                // repaints them this frame. Pings are user-initiated, so this is rare.
                foreach (var window in Resources.FindObjectsOfTypeAll<TabstepProjectWindow>())
                    window.Repaint();
            }
            catch { /* never let a ping throw */ }
        }

        // ---- patch callbacks (run for every ProjectBrowser; gate on IsCompact) ----

        /// <summary>The compact integration applies: hosted, patched and the bar is on.</summary>
        static bool IsCompact(object browser) =>
            _active && HostedBrowsers.Contains(browser) && TabstepSettings.ShowNavigationBar;

        // The toolbar's create button and search field live in Tabstep's bar instead.
        static bool TopToolbarPrefix(object __instance) => !IsCompact(__instance);

        // Removes the "Assets > ..." header row while folder browsing. While searching
        // the row stays — it holds the search scope header.
        static bool GetListHeaderHeightPrefix(object __instance, ref float __result)
        {
            if (!IsCompact(__instance) || ProjectBrowserHost.IsSearching(__instance)) return true;
            __result = 0f;
            return false;
        }

        // Reclaims the skipped toolbar's row: every top-anchored rect moves up by the
        // toolbar height (== the freshly computed tree y) and the columns grow by it.
        static void CalculateRectsPostfix(object __instance)
        {
            if (!IsCompact(__instance)) return;

            var tree = (Rect)TreeViewRectField.GetValue(__instance);
            float dy = tree.y;
            if (dy <= 0f) return;
            tree.y = 0f;
            tree.height += dy;
            TreeViewRectField.SetValue(__instance, tree);

            var list = (Rect)ListAreaRectField.GetValue(__instance);
            list.y -= dy;
            list.height += dy;
            ListAreaRectField.SetValue(__instance, list);

            var header = (Rect)ListHeaderRectField.GetValue(__instance);
            header.y -= dy;
            ListHeaderRectField.SetValue(__instance, header);
        }
    }
}
