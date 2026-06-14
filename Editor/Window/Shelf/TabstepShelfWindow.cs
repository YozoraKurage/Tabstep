using System.Collections.Generic;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// The Tabstep Shelf: a small float window that parks objects mid-flight. Drop
    /// assets (or scene objects) on it, then drag them back out onto another tab, an
    /// Inspector object field, the scene — anywhere Unity drag &amp; drop reaches.
    /// Shift-click selects a range (Ctrl/Cmd-click toggles) and dragging any selected
    /// row carries the whole selection out in one drag.
    ///
    /// A regular floating EditorWindow, treated exactly like the Tabstep window
    /// itself: it moves by its own title tab, floats above the main editor window
    /// (standard Unity z-order for floating windows), can be docked, and survives
    /// restarts through the layout. Ctrl+Shift+D — a global, rebindable shortcut,
    /// with a fallback handler inside the Tabstep window — summons it to the mouse
    /// and drops the current selection onto it.
    ///
    /// With the One-Shot preference on, dragging an item out consumes it: the item
    /// disappears when the drag leaves the shelf and comes back only if the same drag
    /// re-enters and drops it back. The padlock at the left edge of a row exempts the
    /// item from that (and from "Clear"), and keeps it on the shelf for the rest of
    /// the editor session — locked items are stored in SessionState and restored even
    /// if the shelf window itself is closed and reopened.
    /// </summary>
    class TabstepShelfWindow : EditorWindow
    {
        const string DragKey = "Tabstep.ShelfDrag";
        const string KeySeparator = "\n"; // joins item keys in the drag's generic data
        const string LockedSessionKey = "Tabstep.Shelf.LockedItems";
        const float RowHeight = 22f;
        static readonly Vector2 DefaultSize = new Vector2(230, 190);

        static TabstepShelfWindow _instance;

        [SerializeField] List<ShelfItem> _items = new List<ShelfItem>();
        [SerializeField] bool _keepOpen;

        Vector2 _scroll;
        ShelfItem _mouseDownItem;
        // Multi-selection: keys of the selected items, the shift-range anchor, and the
        // item a plain click should collapse the selection to on mouse-up (unless the
        // press turned into a drag first).
        readonly HashSet<string> _selectedKeys = new HashSet<string>();
        string _anchorKey;
        string _collapseKey;
        // One-shot bookkeeping: the item(s) consumed by the current drag-out, restored
        // if the very same drag re-enters the shelf and drops.
        readonly List<ShelfItem> _draggedOut = new List<ShelfItem>();
        string _draggedOutKey;

        public static TabstepShelfWindow Instance
        {
            get
            {
                if (_instance == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<TabstepShelfWindow>();
                    if (all.Length > 0) _instance = all[0];
                }
                return _instance;
            }
        }

        public static bool IsOpen => Instance != null;

        /// <summary>A pinned shelf stays open when its Tabstep window closes.</summary>
        public bool KeepOpen => _keepOpen;

        [MenuItem("YozoLab/Tabstep Shelf")]
        static void OpenFromMenu()
        {
            var anchors = Resources.FindObjectsOfTypeAll<TabstepProjectWindow>();
            ShowNear(anchors.Length > 0 ? anchors[0] : null);
        }

        public static TabstepShelfWindow ShowNear(EditorWindow anchor)
        {
            var window = Instance;
            if (window == null)
            {
                window = CreateNew();
                window.PositionNear(anchor);
            }
            window.Repaint();
            return window;
        }

        /// <summary>
        /// The global Ctrl+Shift+D gesture: brings the shelf (existing or new) to the
        /// mouse and drops the current selection onto it. Rebindable in the Shortcut
        /// Manager; the Tabstep window also handles the same combination itself as a
        /// fallback, in case the binding is shadowed in a given setup.
        /// </summary>
        [Shortcut("Tabstep/Summon Shelf", KeyCode.D, ShortcutModifiers.Action | ShortcutModifiers.Shift)]
        static void SummonShortcut()
        {
            SummonToMouse();
        }

        /// <summary>Summons the shelf to the mouse and adds the current selection to it.</summary>
        internal static void SummonToMouse()
        {
            // Captured before the summon: focusing the shelf window must not change
            // what counts as the current selection.
            var objects = SelectionForShelf();
            var window = SummonAt(GlobalMousePosition());
            if (objects.Length > 0)
                window.AddObjects(objects);
        }

        /// <summary>
        /// What "the current selection" means for the shelf: normally the global
        /// selection — but a Project browser's folder tree pane (the Assets/Packages
        /// column) keeps its selection to itself, never touching the global Selection.
        /// While that tree has keyboard focus, the folders selected there are what the
        /// user is pointing at.
        /// </summary>
        internal static Object[] SelectionForShelf()
        {
            var treeFolders = ProjectBrowserHost.GetFolderTreeSelection();
            return treeFolders.Length > 0 ? treeFolders : Selection.objects;
        }

        /// <summary>Opens the shelf at (or moves it to) a screen point, keeping its size.</summary>
        public static TabstepShelfWindow SummonAt(Vector2 screenPoint)
        {
            // Shortcut handlers run outside any GUI context, where IMGUI's coordinate
            // conversion can silently produce a far-off point from a stale view offset.
            // A window placed there looks exactly like "the shortcut did nothing" —
            // anything implausible lands on the main window instead.
            var main = EditorGUIUtility.GetMainWindowPosition();
            var plausible = Rect.MinMaxRect(main.xMin - 4096, main.yMin - 4096,
                main.xMax + 4096, main.yMax + 4096);
            if (!plausible.Contains(screenPoint))
            {
                // Worth a trace: it means every mouse-position source failed.
                Debug.LogWarning($"[Tabstep] Shelf summon point {screenPoint} looked invalid; " +
                                 "falling back to the main window center.");
                screenPoint = main.center;
            }

            var window = Instance;
            var origin = screenPoint - new Vector2(40f, 12f); // lands under the cursor
            if (window == null)
            {
                window = CreateNew();
                window.position = new Rect(origin, DefaultSize);
            }
            else if (!window.docked)
            {
                // A docked shelf has no position of its own — it only gets focused.
                window.position = new Rect(origin, window.position.size);
            }
            window.Focus();
            window.Repaint();
            return window;
        }

        static TabstepShelfWindow CreateNew()
        {
            var window = CreateInstance<TabstepShelfWindow>();
            // Show(): a regular floating editor window — native dragging by its title
            // tab, always above the main window, dockable, saved into the layout.
            window.Show();
            return window;
        }

        /// <summary>
        /// Best available mouse position in screen coordinates. On Windows the OS
        /// cursor is asked directly — the one source that works in a shortcut handler,
        /// which runs outside any GUI context. Elsewhere Event.current is used when
        /// alive, degrading to the hovered window, then the main window.
        /// </summary>
        static Vector2 GlobalMousePosition()
        {
#if UNITY_EDITOR_WIN
            // GetCursorPos reports physical pixels; editor rects use scaled points.
            if (GetCursorPos(out var cursor))
                return new Vector2(cursor.x, cursor.y) / EditorGUIUtility.pixelsPerPoint;
#endif
            try
            {
                if (Event.current != null)
                    return GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
            }
            catch
            {
                // No live GUI clip stack — fall through to the window-based guesses.
            }
            var hovered = mouseOverWindow;
            if (hovered != null) return hovered.position.center;
            return EditorGUIUtility.GetMainWindowPosition().center;
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        struct Win32Point
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out Win32Point point);
#endif

        public static void Toggle(EditorWindow anchor)
        {
            if (IsOpen) Instance.Close();
            else ShowNear(anchor);
        }

        public void AddObjects(IEnumerable<Object> objects)
        {
            foreach (var obj in objects)
            {
                var item = ShelfItem.ForObject(obj);
                if (item == null || _items.Exists(existing => existing.Key == item.Key)) continue;
                _items.Add(item);
            }
            Repaint();
        }

        void OnEnable()
        {
            _instance = this;
            titleContent = new GUIContent("Tabstep Shelf");
            minSize = new Vector2(160, 100);
            wantsMouseMove = true;
            RestoreLockedItems();
        }

        void OnDisable()
        {
            if (_instance == this) _instance = null;
        }

        void PositionNear(EditorWindow anchor)
        {
            var main = EditorGUIUtility.GetMainWindowPosition();
            var around = anchor != null ? anchor.position : main;
            var pos = new Vector2(around.xMax - DefaultSize.x - 12, around.yMax - DefaultSize.y - 12);
            pos.x = Mathf.Clamp(pos.x, main.x, Mathf.Max(main.x, main.xMax - DefaultSize.x));
            pos.y = Mathf.Clamp(pos.y, main.y, Mathf.Max(main.y, main.yMax - DefaultSize.y));
            position = new Rect(pos, DefaultSize);
        }

        void OnGUI()
        {
            HandleDragEvents();
            DrawHeader();
            DrawItems();
        }

        // ---- locked-item session persistence ------------------------------------

        [System.Serializable]
        class LockedItemList
        {
            public List<ShelfItem> items = new List<ShelfItem>();
        }

        /// <summary>
        /// Locked items live in SessionState too, so they outlast the window itself
        /// (closed and reopened) for the duration of the editor session.
        /// </summary>
        void SaveLockedItems()
        {
            var list = new LockedItemList { items = _items.FindAll(item => item.Locked) };
            if (list.items.Count == 0) SessionState.EraseString(LockedSessionKey);
            else SessionState.SetString(LockedSessionKey, JsonUtility.ToJson(list));
        }

        void RestoreLockedItems()
        {
            var json = SessionState.GetString(LockedSessionKey, null);
            if (string.IsNullOrEmpty(json)) return;
            var list = JsonUtility.FromJson<LockedItemList>(json);
            if (list?.items == null) return;
            foreach (var item in list.items)
            {
                if (item == null || string.IsNullOrEmpty(item.Key)) continue;
                if (_items.Exists(existing => existing.Key == item.Key)) continue;
                _items.Add(item);
            }
        }

        // ---- chrome ------------------------------------------------------------

        /// <summary>Toolbar row — moving and closing belong to the window's own tab.</summary>
        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            _keepOpen = GUILayout.Toggle(_keepOpen,
                new GUIContent("Pin", "Keep the shelf open when the Tabstep window closes"),
                EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            using (new EditorGUI.DisabledScope(!_items.Exists(item => !item.Locked)))
                if (GUILayout.Button(new GUIContent("Clear", "Remove all unlocked items"),
                        EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    _items.RemoveAll(item => !item.Locked);
                    _selectedKeys.RemoveWhere(key => !_items.Exists(item => item.Key == key));
                }
            EditorGUILayout.EndHorizontal();
        }

        // ---- items -------------------------------------------------------------

        void DrawItems()
        {
            if (_items.Count == 0)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Drop assets or scene objects here,\n" +
                                           "then drag them out anywhere —\n" +
                                           "other tabs, Inspector fields, the scene.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.Height(50));
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                // Newest first; DrawItem returns false when the item was removed.
                if (!DrawItem(_items[i]))
                {
                    EditorGUILayout.EndScrollView();
                    GUIUtility.ExitGUI();
                    return;
                }
            }
            EditorGUILayout.EndScrollView();

            // A left-click that no row claimed lands on empty space: drop the selection.
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && _selectedKeys.Count > 0)
            {
                _selectedKeys.Clear();
                _anchorKey = null;
                Repaint();
            }
        }

        bool DrawItem(ShelfItem item)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
            var lockRect = new Rect(rect.x + 3, rect.y + (rect.height - 14) / 2, 14, 14);
            var removeRect = new Rect(rect.xMax - 20, rect.y + (rect.height - 16) / 2, 16, 16);
            var obj = item.Resolve();
            bool alive = obj != null;
            bool selected = _selectedKeys.Contains(item.Key);

            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                if (selected)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.49f, 0.91f, 0.35f));
                else if (rect.Contains(e.mousePosition))
                    EditorGUI.DrawRect(rect, new Color(0.5f, 0.7f, 1f, 0.15f));
                var icon = alive ? AssetPreview.GetMiniThumbnail(obj) : null;
                if (icon != null)
                    GUI.DrawTexture(new Rect(rect.x + 21, rect.y + (rect.height - 16) / 2, 16, 16),
                        icon, ScaleMode.ScaleToFit);
                var labelRect = new Rect(rect.x + 41, rect.y, rect.width - 65, rect.height);
                var style = alive ? EditorStyles.label : EditorStyles.centeredGreyMiniLabel;
                var label = alive ? item.DisplayName : item.DisplayName + " (missing)";
                GUI.Label(labelRect, new GUIContent(label, item.AssetPath ?? label), style);
            }
            if (e.type == EventType.MouseMove && rect.Contains(e.mousePosition))
                Repaint();

            // The same padlock the Inspector uses; only visible when locked or hovered.
            bool locked = GUI.Toggle(lockRect, item.Locked,
                new GUIContent("", item.Locked
                    ? "Unlock"
                    : "Lock — keep on the shelf for this editor session"),
                "IN LockButton");
            if (locked != item.Locked)
            {
                item.Locked = locked;
                SaveLockedItems();
            }

            using (new EditorGUI.DisabledScope(item.Locked))
                if (GUI.Button(removeRect,
                        new GUIContent("×", item.Locked ? "Unlock to remove" : "Remove from the shelf"),
                        EditorStyles.miniLabel))
                {
                    _items.Remove(item);
                    _selectedKeys.Remove(item.Key);
                    return false;
                }

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && rect.Contains(e.mousePosition):
                    _mouseDownItem = item;
                    UpdateSelectionOnMouseDown(item, e);
                    e.Use();
                    break;
                case EventType.MouseDown when e.button == 1 && rect.Contains(e.mousePosition):
                    e.Use();
                    ShowItemContextMenu(item, obj);
                    break;
                case EventType.MouseUp:
                    // A plain click released without dragging collapses the selection
                    // to the pressed item (the press alone must not, or multi-drags
                    // die). Every row sees the MouseUp, so this keys off _collapseKey
                    // alone — _mouseDownItem may already be nulled by an earlier row.
                    if (_collapseKey != null)
                    {
                        _selectedKeys.Clear();
                        _selectedKeys.Add(_collapseKey);
                        _collapseKey = null;
                        Repaint();
                    }
                    _mouseDownItem = null;
                    break;
                case EventType.MouseDrag when _mouseDownItem == item && alive:
                    _mouseDownItem = null;
                    _collapseKey = null;
                    StartItemDrag(item);
                    e.Use();
                    break;
            }
            return true;
        }

        /// <summary>
        /// Click selects, Shift-click extends a range from the anchor, Ctrl/Cmd-click
        /// toggles. A plain press on an already-selected row keeps the selection so it
        /// can be dragged out together.
        /// </summary>
        void UpdateSelectionOnMouseDown(ShelfItem item, Event e)
        {
            _collapseKey = null;
            if (e.shift && _anchorKey != null)
            {
                int anchor = _items.FindIndex(existing => existing.Key == _anchorKey);
                int target = _items.FindIndex(existing => existing.Key == item.Key);
                if (anchor < 0) anchor = target;
                _selectedKeys.Clear();
                for (int i = Mathf.Min(anchor, target); i <= Mathf.Max(anchor, target); i++)
                    _selectedKeys.Add(_items[i].Key);
            }
            else if (EditorGUI.actionKey)
            {
                if (!_selectedKeys.Add(item.Key)) _selectedKeys.Remove(item.Key);
                _anchorKey = item.Key;
            }
            else
            {
                if (_selectedKeys.Contains(item.Key))
                {
                    // Defer collapsing until mouse-up; this press may become a drag.
                    _collapseKey = item.Key;
                }
                else
                {
                    _selectedKeys.Clear();
                    _selectedKeys.Add(item.Key);
                }
                _anchorKey = item.Key;
            }
            Repaint();
        }

        /// <summary>
        /// Drags the pressed item out — together with the rest of the selection when
        /// the pressed item is part of it.
        /// </summary>
        void StartItemDrag(ShelfItem pressed)
        {
            var dragItems = _selectedKeys.Contains(pressed.Key) && _selectedKeys.Count > 1
                ? _items.FindAll(item => _selectedKeys.Contains(item.Key))
                : new List<ShelfItem> { pressed };
            var objects = new List<Object>();
            var paths = new List<string>();
            var keys = new List<string>();
            foreach (var item in dragItems)
            {
                var obj = item.Resolve();
                if (obj == null) continue;
                objects.Add(obj);
                keys.Add(item.Key);
                if (item.AssetPath != null) paths.Add(item.AssetPath);
            }
            if (objects.Count == 0) return;
            _draggedOut.Clear();
            _draggedOutKey = null;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objects.ToArray();
            if (paths.Count > 0) DragAndDrop.paths = paths.ToArray();
            DragAndDrop.SetGenericData(DragKey, string.Join(KeySeparator, keys));
            DragAndDrop.StartDrag(objects.Count == 1
                ? objects[0].name
                : $"Shelf ({objects.Count} items)");
        }

        /// <summary>
        /// Per-item actions. For components this is the relay the drag can't do:
        /// pasting a copy onto the selected GameObjects.
        /// </summary>
        void ShowItemContextMenu(ShelfItem item, Object obj)
        {
            var menu = new GenericMenu();
            if (obj != null)
            {
                menu.AddItem(new GUIContent("Ping"), false, () => EditorGUIUtility.PingObject(obj));
                menu.AddItem(new GUIContent("Select"), false, () => Selection.activeObject = obj);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Ping"));
                menu.AddDisabledItem(new GUIContent("Select"));
            }
            if (obj is Component component)
            {
                menu.AddSeparator("");
                int targets = Selection.gameObjects.Length;
                if (targets > 0)
                    menu.AddItem(new GUIContent($"Add To Selected GameObject{(targets == 1 ? "" : "s")}"),
                        false, () =>
                        {
                            foreach (var go in Selection.gameObjects)
                            {
                                ComponentUtility.CopyComponent(component);
                                ComponentUtility.PasteComponentAsNew(go);
                            }
                        });
                else
                    menu.AddDisabledItem(new GUIContent("Add To Selected GameObjects"));
                menu.AddItem(new GUIContent("Copy Component"), false,
                    () => ComponentUtility.CopyComponent(component));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(item.Locked ? "Unlock" : "Lock"), item.Locked, () =>
            {
                item.Locked = !item.Locked;
                SaveLockedItems();
                Repaint();
            });
            if (item.Locked)
                menu.AddDisabledItem(new GUIContent("Remove"));
            else
                menu.AddItem(new GUIContent("Remove"), false, () =>
                {
                    _items.Remove(item);
                    _selectedKeys.Remove(item.Key);
                    Repaint();
                });
            menu.ShowAsContext();
        }

        // ---- drag & drop in/out --------------------------------------------------

        void HandleDragEvents()
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                {
                    var key = DragAndDrop.GetGenericData(DragKey) as string;
                    if (key != null && key == _draggedOutKey && _draggedOut.Count > 0)
                    {
                        // The one-shot drag came back: let it drop to restore the item(s).
                        DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                        if (e.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            _items.AddRange(_draggedOut);
                            _draggedOut.Clear();
                            _draggedOutKey = null;
                            e.Use();
                            // The row count changed mid-pass; abort before the stale layout draws.
                            GUIUtility.ExitGUI();
                        }
                        e.Use();
                        break;
                    }
                    if (key != null)
                    {
                        // Our own item hovering its own shelf — nothing to do with it here.
                        DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        e.Use();
                        break;
                    }
                    if (DragAndDrop.objectReferences.Length == 0) break;
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        AddObjects(DragAndDrop.objectReferences);
                        e.Use();
                        // The row count changed mid-pass; abort before the stale layout draws.
                        GUIUtility.ExitGUI();
                    }
                    e.Use();
                    break;
                }
                case EventType.DragExited:
                    // Our drag left the shelf (heading for its target). With One-Shot on,
                    // that consumes the item(s) — they return only if the drag re-enters.
                    // A drag released inside the shelf (a cancelled pick-up) is not an exit.
                    if (!new Rect(0, 0, position.width, position.height).Contains(e.mousePosition))
                        ConsumeDraggedOutItems();
                    break;
            }
        }

        void ConsumeDraggedOutItems()
        {
            if (!TabstepSettings.ShelfOneShot) return;
            var key = DragAndDrop.GetGenericData(DragKey) as string;
            if (key == null) return;
            var keys = new HashSet<string>(key.Split(KeySeparator[0]));
            // Locked items are exempt: they stay on the shelf however often they leave.
            var taken = _items.FindAll(existing => keys.Contains(existing.Key) && !existing.Locked);
            if (taken.Count == 0) return;
            foreach (var item in taken)
                _items.Remove(item);
            _draggedOut.Clear();
            _draggedOut.AddRange(taken);
            _draggedOutKey = key;
            Repaint();
            // An emptied unpinned shelf folds away once the drag has finished.
            if (_items.Count == 0 && !_keepOpen)
                EditorApplication.delayCall += () =>
                {
                    if (this != null && _items.Count == 0 && !_keepOpen) Close();
                };
        }
    }
}
