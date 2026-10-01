using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Puts Tabstep's "Open Folder in Explorer" where Unity keeps its "Show in Explorer" /
    /// "Reveal in Finder" entry, and takes that one away. The replacement opens the folder
    /// the Project window is showing, with its contents in front of you and whatever is
    /// selected left out of it — which is the thing Unity's entry could never do: it reveals
    /// the selection, so a folder arrives selected inside its parent and an empty-space
    /// right-click lands on the project root.
    ///
    /// Unity's entry is registered natively and can be neither patched nor extended, so it
    /// is removed and ours registered through UnityEditor.Menu's internal
    /// Add/RemoveMenuItem — the pair Unity itself uses to build Window &gt; Layouts and to
    /// drop menu items belonging to excluded modules. That reaches every Project window,
    /// stock or hosted by Tabstep, and the main Assets menu with it. Tabstep's own windows
    /// need it: the type-column view's context menu is Unity's stock Assets popup, which no
    /// GenericMenu item of ours could be added to.
    ///
    /// Switched off in Preferences before anything was installed, the menu is left
    /// untouched. Switched off after the swap, ours stays and reveals the selection the way
    /// Unity's did — Unity's cannot be put back short of an editor restart.
    /// </summary>
    static class ShowInExplorerMenu
    {
        // Where Unity's own entry lives, by platform. Menu paths are the untranslated keys —
        // localization happens where they are drawn — so this holds in a localized editor
        // too. All three names were read off a running editor: "Open Containing Folder" from
        // this package's Linux dev container, "Show in Explorer" from a user's Windows editor
        // (it comes back under that name as soon as the preference is switched off).
        const string StockPath =
#if UNITY_EDITOR_WIN
            "Assets/Show in Explorer";
#elif UNITY_EDITOR_OSX
            "Assets/Reveal in Finder";
#else
            "Assets/Open Containing Folder";
#endif

        // Tabstep's entry, under the platform's own wording for the file browser.
        static string OurPath => "Assets/" + FileBrowser.OpenFolderLabel;

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
            try
            {
                // Ours goes in at the place Unity's entry held — the position is measured
                // first, while the entry is still Unity's — and Unity's comes out.
                //
                // Measured without first asking whether it is there, too: on Windows Unity's
                // entry answers no to MenuItemExists and is absent from GetMenuItems, yet the
                // menu shows it, and it goes away the moment anything else is added to the
                // Assets menu (user's editor, 2026-10-02). Asking first left Tabstep putting
                // its entry at the end of the menu while the real one vanished behind it.
                //
                // Registered even while the preference is off: Unity's cannot be put back
                // once removed, so ours stands in for it (see OpenShownFolder).
                Register(OurPath, RegisterAt(StockPath), OpenShownFolderAction);
                SessionState.SetBool(ReplacedKey, true);
                // Where the menu does admit to Unity's entry, it has to be taken out by name,
                // or the two sit side by side saying nearly the same thing.
                if (OurPath != StockPath && MenuItemExists(StockPath)) RemoveMenuItem(StockPath);
                if (!MenuItemExists(OurPath))
                    Debug.LogWarning($"[Tabstep] The \"{OurPath}\" entry could not be registered.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not adapt the file browser menu entry: {e}");
            }
        }

        /// <summary>
        /// Priority to claim <paramref name="path"/> at: the one that keeps Unity's entry
        /// where it was when the menu admits to having it, and the head of the menu's first
        /// group — the same place, and the only one we can aim at — when it does not.
        ///
        /// Measured once per editor session and remembered. Measuring again would be
        /// measuring our own work: the menu now reports the priority we gave the entry, and
        /// each pass would subtract another one, walking it up the menu a place per domain
        /// reload (19, 18, 17 ... — seen before this was stored).
        /// </summary>
        static int RegisterAt(string path)
        {
            int stored = SessionState.GetInt(PriorityKey, int.MinValue);
            if (stored != int.MinValue) return stored;
            int priority = MenuItemExists(path) ? RegisterPriority(path) : TopGroupPriority(path);
            SessionState.SetInt(PriorityKey, priority);
            return priority;
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

        /// <summary>An entry of the Assets menu itself rather than of one of its submenus.</summary>
        static bool IsTopLevel(string path) => path.Count(c => c == '/') <= 1;

        /// <summary>
        /// What Unity's own entry did, for a preference switched off mid-session: reveal the
        /// selection, which for a folder means selecting it inside its parent.
        /// </summary>
        static void StockReveal()
        {
            EditorUtility.RevealInFinder(
                FileBrowser.ToAbsolutePath(SelectedAssetPath() ?? ProjectPaths.AssetsRoot));
        }

        /// <summary>
        /// The entry: the folder the Project window is showing, whatever happens to be
        /// selected — the one thing Unity's own reveal could not do. With the preference off
        /// it stands in for Unity's entry instead, which cannot be put back before a restart,
        /// and reveals the selection the way that one did.
        /// </summary>
        static void OpenShownFolder()
        {
            if (!TabstepSettings.ShowInExplorerOpensFolders)
            {
                StockReveal();
                return;
            }
            var folder = ProjectBrowserHost.GetLastInteractedFolderPath();
            if (FileBrowser.OpenFolder(folder)) return;
            // A virtual root such as "Packages", which has no folder of its own, or a folder
            // deleted meanwhile — let Unity point the file browser at whatever it resolves.
            EditorUtility.RevealInFinder(FileBrowser.ToAbsolutePath(folder ?? ProjectPaths.AssetsRoot));
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
