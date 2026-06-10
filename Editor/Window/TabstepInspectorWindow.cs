// Parked: the tabbed inspector is temporarily withdrawn from the Unity UI while its
// integration strategy is reworked in a separate scope. Add TABSTEP_INSPECTOR to the
// project's Scripting Define Symbols to bring it back.
#if TABSTEP_INSPECTOR
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// An Inspector with Windows Explorer style tabs: each tab is locked to one
    /// object (asset or scene object) and renders a full inspector for it via
    /// <see cref="InspectorTargetView"/>, independent of the global selection.
    /// Tabs open from drag &amp; drop onto the tab bar, the + button / Ctrl+T,
    /// or the Assets context menu; the pinned Selection tab follows whatever
    /// is currently selected.
    ///
    /// Shortcuts: Ctrl+T open selection as tab, Ctrl+W close tab,
    /// Ctrl(+Shift)+Tab cycle tabs.
    /// </summary>
    public class TabstepInspectorWindow : EditorWindow
    {
        // Tabs survive domain reloads and editor restarts via window serialization.
        [SerializeField] InspectorTabSession _session = new InspectorTabSession();

        InspectorTargetView _view;

        [MenuItem("YozoLab/Tabstep Inspector")]
        public static TabstepInspectorWindow Open()
        {
            var window = GetWindow<TabstepInspectorWindow>();
            window.minSize = new Vector2(300, 250);
            window.Show();
            window.Focus();
            return window;
        }

        [MenuItem("Assets/Open in Tabstep Inspector", false, 21)]
        static void OpenSelectionInTab()
        {
            var window = Open();
            foreach (var obj in Selection.objects)
                window.OpenObjectTab(obj);
        }

        [MenuItem("Assets/Open in Tabstep Inspector", true)]
        static bool ValidateOpenSelectionInTab()
        {
            return Selection.objects.Length > 0;
        }

        /// <summary>The open window, or null — never creates or focuses one.</summary>
        public static TabstepInspectorWindow FindOpenWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<TabstepInspectorWindow>();
            return windows.Length > 0 ? windows[0] : null;
        }

        /// <summary>Shows <paramref name="target"/> in its existing tab or a new one.</summary>
        public void OpenObjectTab(Object target)
        {
            if (target == null) return;
            _session.OpenOrFocusTab(target);
            Repaint();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Tabstep Inspector",
                EditorGUIUtility.IconContent("UnityEditor.InspectorWindow").image);
            _view = new InspectorTargetView();
            // The pinned Selection tab renders whatever is selected — repaint with it.
            Selection.selectionChanged += Repaint;
        }

        void OnDisable()
        {
            Selection.selectionChanged -= Repaint;
            _view?.Dispose();
            _view = null;
        }

        void OnInspectorUpdate()
        {
            // Same cadence as the stock Inspector: pick up component/value changes
            // made elsewhere even while this window is unfocused.
            _view?.Update();
            Repaint();
        }

        void OnGUI()
        {
            HandleShortcuts();
            DrawTabBar();

            var tab = _session.ActiveTab;
            if (Event.current.type == EventType.Layout)
                _view.SetTarget(tab?.Target);

            if (tab == null)
            {
                EditorGUILayout.Space(12);
                EditorGUILayout.HelpBox(
                    "Double-click an object (Project / Hierarchy), drop objects onto the tab bar, " +
                    "or use \"Assets > Open in Tabstep Inspector\".",
                    MessageType.Info);
                return;
            }
            if (tab.FollowsSelection && tab.Target == null)
            {
                EditorGUILayout.Space(12);
                EditorGUILayout.HelpBox("Nothing selected.", MessageType.Info);
                return;
            }

            tab.Scroll = EditorGUILayout.BeginScrollView(tab.Scroll);
            _view.OnGUI(position.width);
            EditorGUILayout.EndScrollView();
        }

        // ---- navigation actions ----------------------------------------------

        void ActivateTab(int index)
        {
            _session.Activate(index);
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
            Repaint();
        }

        void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;
            bool ctrl = e.control || e.command;

            if (ctrl && e.keyCode == KeyCode.T)
            {
                OpenObjectTab(Selection.activeObject);
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.W)
            {
                CloseTab(_session.ActiveIndex);
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.Tab)
            {
                _session.CycleActive(e.shift ? -1 : 1);
                e.Use();
            }
        }

        // ---- tab bar -----------------------------------------------------------

        void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            for (int i = 0; i < _session.Count; i++)
            {
                if (DrawTab(i))
                {
                    // Structure changed mid-loop (a tab closed) — bail out of this pass.
                    EditorGUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
            }
            if (GUILayout.Button(new GUIContent("+", "Open the selected object as a tab (Ctrl+T)"),
                    EditorStyles.toolbarButton, GUILayout.Width(26)))
                OpenObjectTab(Selection.activeObject);
            GUILayout.FlexibleSpace();

            // Pinned tab that follows the selection — with it on, this window stands
            // in for the stock Inspector and the stock one can be closed.
            bool hasSelectionTab = _session.SelectionTabIndex >= 0;
            bool wantSelectionTab = GUILayout.Toggle(hasSelectionTab,
                new GUIContent("Selection", "Pinned tab that follows the current selection"),
                EditorStyles.toolbarButton, GUILayout.Width(64));
            if (wantSelectionTab != hasSelectionTab)
            {
                if (wantSelectionTab) _session.EnsureSelectionTab();
                else _session.CloseSelectionTab();
                // Structure changed mid-pass — bail out, same as a tab close.
                EditorGUILayout.EndHorizontal();
                GUIUtility.ExitGUI();
                return;
            }
            EditorGUILayout.EndHorizontal();
            HandleTabBarDragAndDrop(GUILayoutUtility.GetLastRect());
        }

        /// <summary>Draws one tab. Returns true when the tab was closed (layout is now stale).</summary>
        bool DrawTab(int index)
        {
            var tab = _session.Tabs[index];
            bool active = index == _session.ActiveIndex;
            string title = ProjectPaths.Ellipsize(tab.DisplayName, TabstepSettings.MaxTabTitleLength);
            string tooltip = tab.FollowsSelection
                ? "Follows the current selection"
                : tab.IsAlive
                    ? ObjectTooltip(tab.Target)
                    : "The object was deleted";
            // Trailing spaces reserve room for the close glyph drawn over the active tab.
            var content = new GUIContent(active ? title + "    " : title,
                tab.IsAlive ? AssetPreview.GetMiniThumbnail(tab.Target) : null,
                tooltip);

            var style = EditorStyles.toolbarButton;
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.MaxWidth(200));
            var closeRect = new Rect(rect.xMax - 18, rect.y + (rect.height - 16) / 2, 16, 16);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                if (active && e.button == 0 && closeRect.Contains(e.mousePosition))
                {
                    e.Use();
                    CloseTab(index);
                    return true;
                }
                if (e.button == 2 && TabstepSettings.MiddleClickClosesTab)
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

            if (GUI.Toggle(rect, active, content, style) && !active)
                ActivateTab(index);
            if (active)
                GUI.Label(closeRect, new GUIContent("×", "Close tab (Ctrl+W)"), EditorStyles.miniLabel);
            return false;
        }

        static string ObjectTooltip(Object target)
        {
            var path = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(path)
                ? $"{target.name} ({target.GetType().Name})"
                : path;
        }

        void ShowTabContextMenu(int index)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Close Tab"), false, () => CloseTab(index));
            if (_session.Count > 1)
                menu.AddItem(new GUIContent("Close Other Tabs"), false, () =>
                {
                    _session.CloseOthers(index);
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Close Other Tabs"));
            if (index < _session.Count - 1)
                menu.AddItem(new GUIContent("Close Tabs to the Right"), false, () =>
                {
                    _session.CloseToRight(index);
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Close Tabs to the Right"));
            menu.AddSeparator("");

            var sourceTab = _session.Tabs[index];
            if (sourceTab.Target != null)
                menu.AddItem(new GUIContent(sourceTab.FollowsSelection
                        ? "Lock Selection as Tab" // freeze what the Selection tab shows
                        : "Duplicate Tab"), false, () =>
                {
                    _session.InsertAfter(index, sourceTab.Clone());
                    Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent(sourceTab.FollowsSelection
                    ? "Lock Selection as Tab"
                    : "Duplicate Tab"));

            var target = _session.Tabs[index].Target;
            if (target != null)
            {
                menu.AddItem(new GUIContent("Select && Ping"), false, () =>
                {
                    Selection.activeObject = target;
                    EditorGUIUtility.PingObject(target);
                });
                var folder = ContainingFolder(target);
                if (folder != null)
                    menu.AddItem(new GUIContent("Show in Tabstep"), false,
                        () => TabstepProjectWindow.Open().OpenInNewTab(folder));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Select && Ping"));
            }
            menu.ShowAsContext();
        }

        static string ContainingFolder(Object target)
        {
            var path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path)) return null;
            var folder = AssetDatabase.IsValidFolder(path) ? path : ProjectPaths.GetParent(path);
            return folder != null && AssetDatabase.IsValidFolder(folder) ? folder : null;
        }

        /// <summary>Dropping objects onto the tab bar opens each of them as a tab.</summary>
        void HandleTabBarDragAndDrop(Rect barRect)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!barRect.Contains(e.mousePosition)) return;
            if (DragAndDrop.objectReferences.Length == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                    OpenObjectTab(obj);
            }
            e.Use();
        }
    }
}
#endif
