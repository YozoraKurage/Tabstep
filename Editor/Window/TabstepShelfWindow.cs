using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// The Tabstep Shelf: a small borderless float window that parks objects mid-flight.
    /// Drop assets (or scene objects) on it, then drag them back out onto another tab,
    /// an Inspector object field, the scene — anywhere Unity drag &amp; drop reaches.
    ///
    /// Shown with ShowPopup so it never steals focus while switching tabs underneath it.
    /// Items survive domain reloads via window serialization; the editor-restart ghost
    /// a popup window would otherwise leave behind is detected via SessionState (which
    /// is cleared on restart, matching the shelf's "temporary" semantics) and closed.
    ///
    /// With the One-Shot preference on, dragging an item out consumes it: the item
    /// disappears when the drag leaves the shelf and comes back only if the same drag
    /// re-enters and drops it back.
    /// </summary>
    class TabstepShelfWindow : EditorWindow
    {
        const string DragKey = "Tabstep.ShelfDrag";
        const string AllKey = "*"; // generic-data value for the drag-all handle
        const string AliveKey = "Tabstep.ShelfAlive";
        const float RowHeight = 22f;
        static readonly Vector2 DefaultSize = new Vector2(230, 190);

        static TabstepShelfWindow _instance;

        [SerializeField] List<ShelfItem> _items = new List<ShelfItem>();
        [SerializeField] bool _keepOpen;

        Vector2 _scroll;
        ShelfItem _mouseDownItem;
        bool _dragAllArmed;
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
                window = CreateInstance<TabstepShelfWindow>();
                window.PositionNear(anchor);
                window.ShowPopup();
                SessionState.SetBool(AliveKey, true);
            }
            window.Repaint();
            return window;
        }

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
            // A popup re-created from a saved layout at editor startup is a ghost:
            // SessionState is empty again, while a domain reload would have kept it.
            if (!SessionState.GetBool(AliveKey, false))
                EditorApplication.delayCall += () =>
                {
                    if (this != null) Close();
                };
        }

        void OnDisable()
        {
            if (_instance == this) _instance = null;
        }

        void OnDestroy()
        {
            SessionState.SetBool(AliveKey, false);
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
            DrawBorder();
        }

        // ---- chrome ------------------------------------------------------------

        /// <summary>Title row. A popup has no OS title bar, so this row also moves the window.</summary>
        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Shelf", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            DrawDragAllHandle();
            GUILayout.FlexibleSpace();
            DrawMoveToTabButton();
            _keepOpen = GUILayout.Toggle(_keepOpen,
                new GUIContent("Pin", "Keep the shelf open when the Tabstep window closes"),
                EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            using (new EditorGUI.DisabledScope(_items.Count == 0))
                if (GUILayout.Button(new GUIContent("Clear", "Remove all items"),
                        EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    _items.Clear();
            if (GUILayout.Button(new GUIContent("×", "Close the shelf"),
                    EditorStyles.toolbarButton, GUILayout.Width(22)))
            {
                Close();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            var headerRect = GUILayoutUtility.GetLastRect();
            var e = Event.current;
            // Dragging the empty part of the header moves the popup window.
            if (e.type == EventType.MouseDrag && e.button == 0 &&
                _mouseDownItem == null && !_dragAllArmed &&
                headerRect.Contains(e.mousePosition - e.delta))
            {
                position = new Rect(position.position + e.delta, position.size);
                e.Use();
            }
        }

        /// <summary>Drag this handle to carry every (living) item out in one drag.</summary>
        void DrawDragAllHandle()
        {
            if (_items.Count == 0) return;
            var content = new GUIContent("≡", "Drag all items out at once");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarButton, GUILayout.Width(24));
            GUI.Label(rect, content, EditorStyles.toolbarButton);
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && rect.Contains(e.mousePosition):
                    _dragAllArmed = true;
                    e.Use();
                    break;
                case EventType.MouseUp:
                    _dragAllArmed = false;
                    break;
                case EventType.MouseDrag when _dragAllArmed:
                    _dragAllArmed = false;
                    StartDragAll();
                    e.Use();
                    break;
            }
        }

        void StartDragAll()
        {
            var objects = new List<Object>();
            var paths = new List<string>();
            foreach (var item in _items)
            {
                var obj = item.Resolve();
                if (obj == null) continue;
                objects.Add(obj);
                if (item.AssetPath != null) paths.Add(item.AssetPath);
            }
            if (objects.Count == 0) return;
            _draggedOut.Clear();
            _draggedOutKey = null;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objects.ToArray();
            if (paths.Count > 0) DragAndDrop.paths = paths.ToArray();
            DragAndDrop.SetGenericData(DragKey, AllKey);
            DragAndDrop.StartDrag($"Shelf ({objects.Count} items)");
        }

        /// <summary>Hands every asset item over to the active Tabstep tab's folder.</summary>
        void DrawMoveToTabButton()
        {
            var windows = Resources.FindObjectsOfTypeAll<TabstepProjectWindow>();
            var target = windows.Length > 0 ? windows[0] : null;
            var assetPaths = new List<string>();
            foreach (var item in _items)
                if (item.AssetPath != null && item.Resolve() != null)
                    assetPaths.Add(item.AssetPath);
            using (new EditorGUI.DisabledScope(target == null || target.ActiveFolderPath == null ||
                                               assetPaths.Count == 0))
            {
                if (!GUILayout.Button(new GUIContent("→ Tab",
                            "Move all asset items into the active tab's folder"),
                        EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    return;
                var folder = target.ActiveFolderPath;
                if (target.MoveAssetsToActiveFolder(assetPaths) > 0)
                    // Hand-off complete: drop everything that now lives in that folder.
                    _items.RemoveAll(item => item.AssetPath != null &&
                                             ProjectPaths.GetParent(item.AssetPath) == folder);
            }
        }

        /// <summary>Popup windows have no frame; a 1px outline keeps the shelf readable.</summary>
        void DrawBorder()
        {
            if (Event.current.type != EventType.Repaint) return;
            var color = EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.4f, 0.4f, 0.4f);
            var full = new Rect(0, 0, position.width, position.height);
            EditorGUI.DrawRect(new Rect(full.x, full.y, full.width, 1), color);
            EditorGUI.DrawRect(new Rect(full.x, full.yMax - 1, full.width, 1), color);
            EditorGUI.DrawRect(new Rect(full.x, full.y, 1, full.height), color);
            EditorGUI.DrawRect(new Rect(full.xMax - 1, full.y, 1, full.height), color);
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
        }

        bool DrawItem(ShelfItem item)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
            var removeRect = new Rect(rect.xMax - 20, rect.y + (rect.height - 16) / 2, 16, 16);
            var obj = item.Resolve();
            bool alive = obj != null;

            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                if (rect.Contains(e.mousePosition))
                    EditorGUI.DrawRect(rect, new Color(0.5f, 0.7f, 1f, 0.15f));
                var icon = alive ? AssetPreview.GetMiniThumbnail(obj) : null;
                if (icon != null)
                    GUI.DrawTexture(new Rect(rect.x + 4, rect.y + (rect.height - 16) / 2, 16, 16),
                        icon, ScaleMode.ScaleToFit);
                var labelRect = new Rect(rect.x + 24, rect.y, rect.width - 48, rect.height);
                var style = alive ? EditorStyles.label : EditorStyles.centeredGreyMiniLabel;
                var label = alive ? item.DisplayName : item.DisplayName + " (missing)";
                GUI.Label(labelRect, new GUIContent(label, item.AssetPath ?? label), style);
            }
            if (e.type == EventType.MouseMove && rect.Contains(e.mousePosition))
                Repaint();

            if (GUI.Button(removeRect, new GUIContent("×", "Remove from the shelf"), EditorStyles.miniLabel))
            {
                _items.Remove(item);
                return false;
            }

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && rect.Contains(e.mousePosition):
                    _mouseDownItem = item;
                    e.Use();
                    break;
                case EventType.MouseDown when e.button == 1 && rect.Contains(e.mousePosition):
                    e.Use();
                    ShowItemContextMenu(item, obj);
                    break;
                case EventType.MouseUp:
                    _mouseDownItem = null;
                    break;
                case EventType.MouseDrag when _mouseDownItem == item && alive:
                    _mouseDownItem = null;
                    _draggedOut.Clear();
                    _draggedOutKey = null;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new[] { obj };
                    var path = item.AssetPath;
                    if (path != null) DragAndDrop.paths = new[] { path };
                    DragAndDrop.SetGenericData(DragKey, item.Key);
                    DragAndDrop.StartDrag(item.DisplayName);
                    e.Use();
                    break;
            }
            return true;
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
            menu.AddItem(new GUIContent("Remove"), false, () =>
            {
                _items.Remove(item);
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
            var taken = key == AllKey
                ? new List<ShelfItem>(_items)
                : _items.FindAll(existing => existing.Key == key);
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
