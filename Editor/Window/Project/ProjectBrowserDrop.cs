using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Hands a drag to Unity's own Project-window drop, aimed at a folder of our choosing.
    ///
    /// The type-column view covers the embedded browser's list pane and swallows every drag
    /// event before the browser sees it, so nothing native ever gets to handle a drop over
    /// it. Re-implementing what the browser does is a losing game: besides moving assets it
    /// copies files in from Finder/Explorer, saves dragged scene objects out as prefabs, and
    /// runs whatever drop handlers other packages registered with
    /// <c>DragAndDrop.AddDropHandler</c>. This makes the same call the stock list area makes
    /// for a drop on the folder it browses (ObjectListLocalGroup.HandleUnusedDragEvents) —
    /// only the target folder differs.
    ///
    /// Both members it needs are internal, so both are reached by reflection and both are
    /// null-guarded: when a future Unity version renames them the caller falls back to
    /// whatever it can do itself.
    /// </summary>
    static class ProjectBrowserDrop
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        // DragAndDrop.DropOnProjectBrowserWindow(int dragUponInstanceID, string dropUponPath, bool perform)
        static readonly MethodInfo DropMethod = typeof(DragAndDrop).GetMethod(
            "DropOnProjectBrowserWindow", Statics, null,
            new[] { typeof(int), typeof(string), typeof(bool) }, null);

        // AssetDatabase.GetMainAssetInstanceID(string) — the folder path to instance id
        // lookup the drop wants, and the one Unity's own list area uses. It also answers
        // for the "Assets" root, whose main asset LoadMainAssetAtPath cannot load.
        static readonly MethodInfo FolderInstanceIDMethod = typeof(AssetDatabase).GetMethod(
            "GetMainAssetInstanceID", Statics, null, new[] { typeof(string) }, null);

        /// <summary>
        /// Runs the drop onto <paramref name="folder"/>, performing it when
        /// <paramref name="perform"/> is set (a <see cref="EventType.DragPerform"/>) and
        /// merely asking what it would do otherwise. False means the native route is
        /// unavailable and the caller is on its own; <paramref name="mode"/> is then
        /// untouched.
        /// </summary>
        public static bool TryDrop(string folder, bool perform, out DragAndDropVisualMode mode)
        {
            mode = DragAndDropVisualMode.None;
            if (DropMethod == null || string.IsNullOrEmpty(folder)) return false;
            int instanceID = FolderInstanceID(folder);
            if (instanceID == 0) return false;
            try
            {
                mode = (DragAndDropVisualMode)DropMethod.Invoke(null,
                    new object[] { instanceID, folder, perform });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not drop onto \"{folder}\": {e}");
                return false;
            }
        }

        static int FolderInstanceID(string folder)
        {
            if (FolderInstanceIDMethod != null)
            {
                try
                {
                    var id = (int)FolderInstanceIDMethod.Invoke(null, new object[] { folder });
                    if (id != 0) return id;
                }
                catch
                {
                    // Fall through to the public lookup below.
                }
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(folder);
            return asset != null ? asset.GetInstanceID() : 0;
        }
    }
}
