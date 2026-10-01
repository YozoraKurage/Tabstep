using System;
using System.Collections.Generic;
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
        // Platform-specific wording of the stock entry: Windows, macOS, Linux. Menu paths are
        // the untranslated keys — localization happens where they are drawn — so this holds
        // in a localized editor too. Checked against the running editor on Linux, where the
        // entry really is called "Open Containing Folder" (2026-10-01); missing that name
        // left Tabstep adding a second entry of its own there instead of rewiring Unity's.
        static readonly string[] StockPaths =
        {
            "Assets/Show in Explorer",
            "Assets/Reveal in Finder",
            "Assets/Open Containing Folder",
        };

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
            ScheduleRecheck();
        }

        // Not every entry of the Assets menu is in place by the first delayCall — other
        // packages are still registering theirs, and Unity's own can arrive late too. One
        // more pass a moment later picks up a reveal entry that was not there the first
        // time, and drops the stand-in added in its absence.
        const double RecheckDelay = 3.0;
        static double _recheckAt;

        static void ScheduleRecheck()
        {
            _recheckAt = EditorApplication.timeSinceStartup + RecheckDelay;
            EditorApplication.update -= Recheck;
            EditorApplication.update += Recheck;
        }

        static void Recheck()
        {
            if (EditorApplication.timeSinceStartup < _recheckAt) return;
            EditorApplication.update -= Recheck;
            Apply();
        }

        // Survives domain reloads but not an editor restart — exactly as long as the native
        // menu keeps the entry we replaced. Once replaced, the stock entry is gone for the
        // session, so ours has to stay registered (standing in for it) even if the
        // preference is switched off; otherwise the menu would call a dead delegate.
        const string ReplacedKey = "Yozolab.Tabstep.RevealMenuReplaced";

        // The priority the replacement went in with, kept beside it (see ReplacementPriority).
        const string PriorityKey = "Yozolab.Tabstep.RevealMenuPriority";

        /// <summary>(Re)installs the entry. Also called when the preference is switched on.</summary>
        internal static void Apply()
        {
            var wanted = TabstepSettings.ShowInExplorerOpensFolders;
            if (!wanted && !SessionState.GetBool(ReplacedKey, false))
                return; // opted out and nothing installed yet — leave the menu alone
            if (RemoveMenuItemMethod == null || AddMenuItemMethod == null) return;
            var ours = "Assets/" + FileBrowser.OpenFolderLabel;
            try
            {
                var stock = FindStockPath();
                if (stock != null)
                {
                    // Registered even while the preference is off: Unity's entry cannot be put
                    // back once removed, so ours has to stand in for it (see Reveal).
                    Register(stock, ReplacementPriority(stock), RevealAction);
                    if (MenuItemExists(stock))
                    {
                        // A stand-in from an earlier pass, before this entry showed up.
                        if (MenuItemExists(ours)) RemoveMenuItem(ours);
                        SessionState.SetBool(ReplacedKey, true);
                        return;
                    }
                    // Removed but not re-registered: the menu would be left without the entry
                    // altogether, and Unity's cannot be put back. Say so, and stand in.
                    Debug.LogWarning($"[Tabstep] Unity's \"{stock}\" entry could not be " +
                                     $"re-registered; \"{ours}\" stands in for it.");
                }
                // No stock entry to rewire — Tabstep contributes its own, at the head of the
                // menu's first group, where Unity's reveal entry lives and the hand looks for
                // it. This one is ours, so switching the preference off can drop it outright.
                if (wanted) Register(ours, TopGroupPriority(ours), OpenShownFolderAction);
                else RemoveMenuItem(ours);
                SessionState.SetBool(ReplacedKey, wanted);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not adapt the file browser menu entry: {e}");
            }
        }

        static bool MenuItemExists(string path)
        {
            if (MenuItemExistsMethod == null) return false;
            try { return (bool)MenuItemExistsMethod.Invoke(null, new object[] { path }); }
            catch { return false; }
        }

        static void RemoveMenuItem(string path) =>
            RemoveMenuItemMethod.Invoke(null, new object[] { path });

        static void Register(string path, int priority, Action action)
        {
            RemoveMenuItemMethod.Invoke(null, new object[] { path });
            AddMenuItemMethod.Invoke(null, new object[] { path, "", false, priority, action, null });
        }

        /// <summary>The stock entry's menu path, or null when this Unity has none of them.</summary>
        static string FindStockPath()
        {
            foreach (var path in StockPaths)
                // True for our own replacement as well, which is what a re-install needs.
                if (MenuItemExists(path)) return path;
            return null;
        }

        /// <summary>
        /// Top-level entries of the Assets menu in the order Unity keeps them, separators
        /// included — they are ordered like any other entry, and the one just before the
        /// reveal entry may well be one. Null when the internal lookup is gone.
        /// </summary>
        static List<(string Path, int Priority)> MenuEntries()
        {
            if (GetMenuItemsMethod == null) return null;
            try
            {
                if (!(GetMenuItemsMethod.Invoke(null, new object[] { "Assets", true, false }) is Array items))
                    return null;
                var itemType = items.GetType().GetElementType();
                var pathProperty = itemType?.GetProperty("path");
                var priorityProperty = itemType?.GetProperty("priority");
                if (pathProperty == null || priorityProperty == null) return null;
                var entries = new List<(string, int)>();
                foreach (var item in items)
                {
                    var path = (string)pathProperty.GetValue(item);
                    // Submenu entries ("Assets/Import Package/Custom Package...") are in this
                    // list too; they sit inside their parent and never precede it.
                    if (!IsTopLevel(path)) continue;
                    entries.Add((path, (int)priorityProperty.GetValue(item)));
                }
                return entries;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Priority to re-register <paramref name="path"/> with, chosen so the entry stays
        /// where it was.
        ///
        /// A menu is ordered by priority first and registration order second, so re-adding an
        /// entry at its own priority drops it to the end of that whole priority run — and in
        /// the Assets menu everything from "Open" to "Export Package..." shares priority 20,
        /// which is most of the menu away from where the reveal entry belongs (measured in
        /// the editor, 2026-10-01). One less puts it back: ahead of the run it used to open,
        /// still behind whatever preceded it, and near enough that no extra separator appears
        /// (Unity draws one at a gap of 11 or more).
        ///
        /// That is exact only for an entry that opened its priority run, which the reveal
        /// entry does on every platform checked. One from the middle of a run cannot be
        /// placed by priority alone; it keeps its own and lands at the end of the run.
        /// </summary>
        static int RegisterPriority(string path)
        {
            var entries = MenuEntries();
            if (entries == null) return FallbackPriority;
            int previous = int.MinValue;
            foreach (var entry in entries)
            {
                if (entry.Path != path)
                {
                    previous = entry.Priority;
                    continue;
                }
                if (entry.Priority < 0) return FallbackPriority;
                return previous < entry.Priority ? entry.Priority - 1 : entry.Priority;
            }
            return FallbackPriority;
        }

        /// <summary>
        /// Priority that puts an entry at the head of the menu's first real group — directly
        /// under Create, which is where Unity's own reveal entry sits and where the hand goes
        /// looking for it. One less than that group's, so it opens the group rather than
        /// closing it, and close enough that Unity draws no separator around it.
        ///
        /// <paramref name="ignorePath"/> is the entry being placed: an earlier pass may have
        /// put it there already, and measuring against it would subtract another one every
        /// time, walking it up the menu a place per domain reload.
        /// </summary>
        static int TopGroupPriority(string ignorePath)
        {
            var entries = MenuEntries();
            if (entries == null) return FallbackPriority;
            int first = int.MinValue;
            foreach (var entry in entries)
            {
                if (entry.Path == ignorePath) continue;
                if (first == int.MinValue) { first = entry.Priority; continue; }
                if (entry.Priority > first) return entry.Priority - 1;
            }
            return first == int.MinValue ? FallbackPriority : first; // a menu of one group
        }

        /// <summary>
        /// Priority to register the replacement with. Measured once per editor session, while
        /// the entry is still Unity's own: afterwards the menu reports the priority we gave
        /// it, and measuring again would subtract another one on every pass.
        /// </summary>
        static int ReplacementPriority(string stock)
        {
            if (SessionState.GetBool(ReplacedKey, false))
            {
                int stored = SessionState.GetInt(PriorityKey, int.MinValue);
                if (stored != int.MinValue) return stored;
            }
            int priority = RegisterPriority(stock);
            SessionState.SetInt(PriorityKey, priority);
            return priority;
        }

        /// <summary>An entry of the Assets menu itself rather than of one of its submenus.</summary>
        static bool IsTopLevel(string path) => path.Count(c => c == '/') <= 1;

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
