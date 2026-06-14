using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Docks one editor window into the layout next to another — the result of
    /// dropping its tab on the target pane's edge, done programmatically. Drives the
    /// editor's internal docking machinery (SplitView.DragOver / PerformDrop) via
    /// reflection, the same code path a manual drag goes through, so the layout
    /// splits and reflows exactly like a hand-made dock. Everything is null-guarded;
    /// callers fall back to floating placement when this returns false (internal API
    /// changed, or the layout offered no drop zone at that point).
    /// </summary>
    static class WindowDocking
    {
        static readonly Assembly EditorAssembly = typeof(UnityEditor.Editor).Assembly;
        static readonly Type SplitViewType = EditorAssembly.GetType("UnityEditor.SplitView");
        static readonly Type DockAreaType = EditorAssembly.GetType("UnityEditor.DockArea");
        static readonly Type ViewType = EditorAssembly.GetType("UnityEditor.View");

        static readonly FieldInfo ParentField =
            typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly PropertyInfo ViewParentProperty =
            ViewType?.GetProperty("parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static readonly PropertyInfo ScreenPositionProperty =
            ViewType?.GetProperty("screenPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // public DropInfo DragOver(EditorWindow w, Vector2 mouseScreenPosition)
        static readonly MethodInfo DragOverMethod = FindMethod(SplitViewType, "DragOver", 2);

        // public bool PerformDrop(EditorWindow dropWindow, DropInfo dropInfo, Vector2 screenPos)
        static readonly MethodInfo PerformDropMethod = FindMethod(SplitViewType, "PerformDrop", 3);

        /// <summary>By name and arity — GetMethod by name alone throws on overloads.</summary>
        static MethodInfo FindMethod(Type type, string name, int paramCount)
        {
            if (type == null) return null;
            foreach (var method in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (method.Name == name && method.GetParameters().Length == paramCount)
                    return method;
            return null;
        }

        // PerformDrop pulls the dropped window out of this dock area — during a manual
        // drag it is the pane the tab was torn from.
        static readonly FieldInfo OriginalDragSourceField =
            DockAreaType?.GetField("s_OriginalDragSource", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        static bool Available =>
            SplitViewType != null && DockAreaType != null && ParentField != null &&
            ViewParentProperty != null && ScreenPositionProperty != null &&
            DragOverMethod != null && PerformDropMethod != null && OriginalDragSourceField != null;

        /// <summary>
        /// Splits the layout at <paramref name="anchor"/>'s right edge and docks
        /// <paramref name="window"/> (already shown, floating) into the new pane.
        /// False when docking is unavailable or the layout rejects the drop.
        /// </summary>
        public static bool DockRightOf(EditorWindow anchor, EditorWindow window)
        {
            if (!Available || anchor == null || window == null) return false;
            try
            {
                var anchorDock = ParentField.GetValue(anchor);
                var windowDock = ParentField.GetValue(window);
                if (anchorDock == null || windowDock == null ||
                    !DockAreaType.IsInstanceOfType(anchorDock) ||
                    !DockAreaType.IsInstanceOfType(windowDock)) return false;
                var splitView = ViewParentProperty.GetValue(anchorDock);
                if (!SplitViewType.IsInstanceOfType(splitView)) return false;

                // A point just inside the vertical middle of the anchor pane's right
                // edge — squarely in the right-edge drop zone, never in a corner.
                var paneRect = (Rect)ScreenPositionProperty.GetValue(anchorDock);
                var point = new Vector2(paneRect.xMax - 4f, paneRect.y + paneRect.height / 2f);

                var dropInfo = DragOverMethod.Invoke(splitView, new object[] { window, point });
                if (dropInfo == null) return false;
                // A DropInfo without userData is the root view claiming the drag with
                // no actual drop zone — performing that drop would throw.
                var userData = dropInfo.GetType()
                    .GetField("userData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(dropInfo);
                if (userData == null) return false;

                var previousSource = OriginalDragSourceField.GetValue(null);
                OriginalDragSourceField.SetValue(null, windowDock);
                try
                {
                    return PerformDropMethod.Invoke(splitView,
                        new object[] { window, dropInfo, point }) is true;
                }
                finally
                {
                    OriginalDragSourceField.SetValue(null, previousSource);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Docking beside the window failed: {e}");
                return false;
            }
        }
    }
}
