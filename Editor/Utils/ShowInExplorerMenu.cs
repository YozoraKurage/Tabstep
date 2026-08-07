using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Rewires Unity's "Show in Explorer" / "Reveal in Finder" entry in the Assets menu so a
    /// FOLDER opens with its contents shown instead of being selected inside its parent, and
    /// so a right-click on empty space — where nothing is selected — targets the folder the
    /// Project window is showing instead of the project root. Files keep Unity's behaviour:
    /// their containing folder opens with the file selected.
    ///
    /// The stock entry is registered natively, so it can be neither patched nor extended:
    /// it is removed and re-registered at the same path and priority through
    /// UnityEditor.Menu's internal Add/RemoveMenuItem — the pair Unity itself uses to build
    /// Window &gt; Layouts and to drop menu items belonging to excluded modules. That reaches
    /// every Project window, stock or hosted by Tabstep, and the main Assets menu with it.
    ///
    /// When the stock entry cannot be found (renamed in a future Unity) Tabstep registers
    /// its own "Open Folder in ..." entry instead, so the type-column view's context menu —
    /// Unity's stock Assets popup, which no GenericMenu item of ours could be added to —
    /// always has a way to open the folder. Switched off in Preferences before anything was
    /// installed, the menu is left untouched; an entry already replaced this session keeps
    /// ours registered — Unity's cannot be put back short of an editor restart — and it then
    /// behaves like the stock one again.
    /// </summary>
    static class ShowInExplorerMenu
    {
        // Platform-specific wording of the stock entry. Menu paths are the untranslated
        // keys — localization happens where they are drawn — so this holds in a localized
        // editor too.
        static readonly string[] StockPaths = { "Assets/Show in Explorer", "Assets/Reveal in Finder" };

        // Used when the real priority cannot be read back; keeps the entry near Unity's own
        // reveal/open group rather than at the end of the menu.
        const int FallbackPriority = 20;

        const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        static readonly MethodInfo MenuItemExistsMethod =
            typeof(Menu).GetMethod("MenuItemExists", Statics, null, new[] { typeof(string) }, null);

        static readonly MethodInfo RemoveMenuItemMethod =
            typeof(Menu).GetMethod("RemoveMenuItem", Statics, null, new[] { typeof(string) }, null);

        // static void AddMenuItem(string name, string shortcut, bool checked, int priority,
        //                         Action execute, Func<bool> validate)
        static readonly MethodInfo AddMenuItemMethod = typeof(Menu).GetMethods(Statics)
            .FirstOrDefault(m => m.Name == "AddMenuItem" && m.GetParameters().Length == 6);

        // static ScriptingMenuItem[] GetMenuItems(string menuPath, bool includeSeparators, bool localized)
        static readonly MethodInfo GetMenuItemsMethod = typeof(Menu).GetMethod("GetMenuItems", Statics,
            null, new[] { typeof(string), typeof(bool), typeof(bool) }, null);

        // Held in statics so the native menu can never call into a collected delegate.
        static readonly Action RevealAction = Reveal;
        static readonly Action OpenShownFolderAction = OpenShownFolder;

        [InitializeOnLoadMethod]
        static void Install()
        {
            // Menus are still being built while InitializeOnLoad runs, and the entry has to
            // be re-registered after every domain reload anyway: the path survives in the
            // native menu, the delegate behind it does not.
            EditorApplication.delayCall += Apply;
        }

        // Survives domain reloads but not an editor restart — exactly as long as the native
        // menu keeps the entry we replaced. Once replaced, the stock entry is gone for the
        // session, so ours has to stay registered (standing in for it) even if the
        // preference is switched off; otherwise the menu would call a dead delegate.
        const string ReplacedKey = "Yozolab.Tabstep.RevealMenuReplaced";

        /// <summary>(Re)installs the entry. Also called when the preference is switched on.</summary>
        internal static void Apply()
        {
            var wanted = TabstepSettings.ShowInExplorerOpensFolders;
            if (!wanted && !SessionState.GetBool(ReplacedKey, false))
                return; // opted out and nothing installed yet — leave the menu alone
            if (RemoveMenuItemMethod == null || AddMenuItemMethod == null) return;
            try
            {
                var stock = FindStockPath();
                if (stock != null)
                {
                    // Registered even while the preference is off: Unity's entry cannot be put
                    // back once removed, so ours has to stand in for it (see Reveal).
                    Register(stock, StockPriority(stock), RevealAction);
                    SessionState.SetBool(ReplacedKey, true);
                    return;
                }
                // No stock entry to rewire — Tabstep contributes its own. This one is ours,
                // so switching the preference off can drop it outright.
                var fallback = "Assets/" + FileBrowser.OpenFolderLabel;
                if (wanted) Register(fallback, FallbackPriority, OpenShownFolderAction);
                else RemoveMenuItemMethod.Invoke(null, new object[] { fallback });
                SessionState.SetBool(ReplacedKey, wanted);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not adapt the \"Show in Explorer\" menu entry: {e}");
            }
        }

        static void Register(string path, int priority, Action action)
        {
            RemoveMenuItemMethod.Invoke(null, new object[] { path });
            AddMenuItemMethod.Invoke(null, new object[] { path, "", false, priority, action, null });
        }

        /// <summary>The stock entry's menu path, or null when this Unity has neither.</summary>
        static string FindStockPath()
        {
            if (MenuItemExistsMethod == null) return null;
            foreach (var path in StockPaths)
            {
                try
                {
                    // True for our own replacement as well, which is what a re-install needs.
                    if ((bool)MenuItemExistsMethod.Invoke(null, new object[] { path })) return path;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>Priority the entry currently sits at, so re-registering does not move it.</summary>
        static int StockPriority(string path)
        {
            if (GetMenuItemsMethod == null) return FallbackPriority;
            try
            {
                if (!(GetMenuItemsMethod.Invoke(null, new object[] { "Assets", false, false }) is Array items))
                    return FallbackPriority;
                var itemType = items.GetType().GetElementType();
                var pathProperty = itemType?.GetProperty("path");
                var priorityProperty = itemType?.GetProperty("priority");
                if (pathProperty == null || priorityProperty == null) return FallbackPriority;
                foreach (var item in items)
                {
                    if ((string)pathProperty.GetValue(item) != path) continue;
                    var priority = (int)priorityProperty.GetValue(item);
                    return priority >= 0 ? priority : FallbackPriority;
                }
            }
            catch
            {
                // Fall through to the default below.
            }
            return FallbackPriority;
        }

        /// <summary>
        /// The rewired entry: a folder — or, with nothing selected, the folder the Project
        /// window shows — opens; a file keeps Unity's reveal, which already opens its
        /// containing folder with the file selected.
        /// </summary>
        static void Reveal()
        {
            if (!TabstepSettings.ShowInExplorerOpensFolders)
            {
                StockReveal();
                return;
            }
            var selected = SelectedAssetPath();
            if (selected != null && !AssetDatabase.IsValidFolder(selected))
            {
                EditorUtility.RevealInFinder(FileBrowser.ToAbsolutePath(selected));
                return;
            }
            var folder = selected ?? ProjectBrowserHost.GetLastInteractedFolderPath();
            if (FileBrowser.OpenFolder(folder)) return;
            // Deleted meanwhile, or a virtual root such as "Packages" that has no folder of
            // its own — let Unity point the file browser at whatever it can resolve.
            EditorUtility.RevealInFinder(FileBrowser.ToAbsolutePath(folder ?? ProjectPaths.AssetsRoot));
        }

        /// <summary>
        /// What Unity's own entry did, for a preference switched off mid-session: reveal the
        /// selection, which for a folder means selecting it inside its parent.
        /// </summary>
        static void StockReveal()
        {
            EditorUtility.RevealInFinder(
                FileBrowser.ToAbsolutePath(SelectedAssetPath() ?? ProjectPaths.AssetsRoot));
        }

        /// <summary>The fallback entry, which only ever opens the folder being browsed.</summary>
        static void OpenShownFolder()
        {
            FileBrowser.OpenFolder(ProjectBrowserHost.GetLastInteractedFolderPath());
        }

        /// <summary>Path of the asset the stock entry would act on, or null when none is selected.</summary>
        static string SelectedAssetPath()
        {
            var active = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(active)) return active;
            // Selection.activeObject is a scene object, or the selection lives only in the
            // Project browser's list — assetGUIDs covers the latter.
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0) return null;
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }
}
