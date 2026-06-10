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
        static bool _initialized;
        static bool _active;

        /// <summary>True when Harmony was found and every patch applied.</summary>
        public static bool Active
        {
            get
            {
                Initialize();
                return _active;
            }
        }

        public static void Register(object browser)
        {
            Initialize();
            if (browser != null) HostedBrowsers.Add(browser);
        }

        public static void Unregister(object browser)
        {
            if (browser != null) HostedBrowsers.Remove(browser);
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
