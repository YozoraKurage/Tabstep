using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>How a tab lays out the items of the folder it shows.</summary>
    enum ItemViewMode
    {
        /// <summary>Unity's own asset list (the embedded browser). Default.</summary>
        Stock,
        /// <summary>Items grouped by type into vertical columns laid out left to right.</summary>
        TypeColumns,
    }

    /// <summary>Sort key for the type-column view, mirroring Windows Explorer's column sorts.</summary>
    enum AssetSortKey
    {
        Name,
        Type,
        DateModified,
        Size,
    }

    /// <summary>
    /// Records the most recent asset ping (<see cref="EditorGUIUtility.PingObject(int)"/>) so
    /// the type-column view can flash the pinged item itself — the stock list draws that flash,
    /// but it sits hidden behind the column view. Fed by a Harmony postfix
    /// (<see cref="ProjectBrowserPatcher"/>); without Harmony there is simply no flash.
    /// </summary>
    static class PingTracker
    {
        public const double Duration = 1.4; // ≈ Unity's ping lifetime

        public static int InstanceID { get; private set; }
        public static double StartTime { get; private set; } = -100;

        public static void Record(int instanceID)
        {
            InstanceID = instanceID;
            StartTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>Asset path of an in-progress ping (with 0..1 progress), or null when none.</summary>
        public static string Active(out float progress)
        {
            progress = 0f;
            if (StartTime < 0) return null;
            double age = EditorApplication.timeSinceStartup - StartTime;
            if (age < 0 || age > Duration) return null;
            var path = AssetDatabase.GetAssetPath(EditorUtility.InstanceIDToObject(InstanceID));
            if (string.IsNullOrEmpty(path)) return null;
            progress = (float)(age / Duration);
            return path;
        }
    }

    /// <summary>
    /// An opt-in replacement for the embedded browser's asset list (the right pane): the
    /// current folder's items grouped by type into vertical columns laid out left to right,
    /// with a horizontal scrollbar to move across the types. Self-rendered in IMGUI so it can
    /// offer sorting and a layout Unity's Project window does not.
    ///
    /// It is drawn over the browser's list area while the browser underneath keeps the folder
    /// tree, search and selection sync working — only the list pane is covered. Each event is
    /// consumed in a pass that runs before the browser paints (so the covered list never also
    /// reacts) and the pixels are drawn in a pass that runs after it (so they land on top).
    ///
    /// One instance per window (no shared mutable state) so multiple Tabstep windows that
    /// happen to be open at once never interfere with each other.
    /// </summary>
    internal sealed class AssetColumnView
    {
        /// <summary>Callbacks into the owning window — navigation and repaint.</summary>
        public struct Host
        {
            public Action<string> OpenFolder;          // double-click a folder column entry
            public Action<string> OpenFolderInNewTab;  // middle-click a folder column entry
            public Action Repaint;
            public Action MarkBrowserInteracted;        // so the Assets/Create menu targets our folder
            // A freshly invoked Assets/Create/... request to drive in the column view
            // (captured by ProjectBrowserPatcher so the embedded browser's own — and
            // invisible — rename overlay never runs). Null when nothing is pending.
            public Func<AssetCreationBridge.Request> TakePendingCreation;
        }

        // ---- layout constants ----
        const float ColumnWidth = 190f;
        const float ColumnGap = 6f;
        const float HeaderHeight = 22f;
        const float RowHeight = 20f;
        const float IconSize = 16f;
        const float Padding = 6f;
        const float ScrollbarThickness = 13f;
        const float DragThreshold = 6f;
        const float MinThumb = 24f;

        // Bumped after any project change so every view rebuilds its cache lazily.
        public static int ProjectVersion;

        Vector2 _scroll;

        // Cache of the grouped/sorted columns; rebuilt only when an input below changes.
        string _builtFolder;
        AssetSortKey _builtKey;
        bool _builtDescending;
        int _builtVersion = -1;
        // The in-flight Assets/Create/... path baked into the last build, so the
        // phantom row appears/disappears when the request arrives or completes.
        string _builtCreationPath;
        readonly List<Column> _columns = new List<Column>();

        // Press / potential-drag state for the list.
        string _pressedPath;
        Vector2 _pressPos;
        bool _maybeDragging;
        // A plain (no-modifier) press defers its selection to release, so that grabbing an
        // item to drag it does not switch the Inspector — only a click that never becomes a
        // drag selects, on mouse up.
        bool _clickSelectPending;

        // Scrollbar thumb drag: 0 none, 1 horizontal, 2 vertical.
        int _thumbDrag;
        float _thumbGrabOffset;

        string _hoverPath;

        // The folder column currently highlighted as a drop target (only while a drag with
        // "Column View Folder Drop" enabled hovers a folder). Cleared when the drag leaves.
        string _dropFolder;

        // Ping flash replicating the stock list's: the pinged asset path and its fade alpha
        // for the current Repaint, plus the ping we have already scrolled into view.
        string _pingPath;
        float _pingAlpha;
        double _pingScrolled = -1;

        // The item a plain/Ctrl click last landed on; Shift+click selects the range from
        // here to the clicked item, in the visual reading order (down each column, then
        // across). Cleared when the shown folder changes.
        string _selectionAnchor;

        // Inline rename overlay (F2), mirroring the stock browser: a text field drawn over
        // the selected row's label that commits on Enter / focus loss, cancels on Escape,
        // keeps the file extension, and reselects the renamed asset. Null path = not renaming.
        const string RenameControlName = "TabstepColumnRename";
        string _renamePath;
        string _renameText;
        bool _renameFocusPending;

        // While naming a brand-new asset from Assets/Create/... this holds the request
        // we drained from AssetCreationBridge. The asset does not exist on disk yet
        // (Unity defers writing it until the inline rename commits), so the column
        // view synthesises a phantom row for the rename overlay to sit on. Commit
        // invokes the captured EndNameEditAction.Action (which writes the asset);
        // Escape or focus loss invokes EndNameEditAction.Cancelled.
        AssetCreationBridge.Request _creation;

        class Column
        {
            public string Label;
            public readonly List<Item> Items = new List<Item>();
        }

        struct Item
        {
            public string Path;
            public string Name;
            public long Size;
            public long DateTicks;
        }

        /// <summary>Forces a rebuild on the next pass (view toggled, sort changed, project changed).</summary>
        public void MarkDirty() => _builtFolder = null;

        // ---- event pass (runs BEFORE the embedded browser paints) ----------------

        public void HandleEvents(Rect listRect, string folder, AssetSortKey key, bool descending, Host host)
        {
            ConsumePendingCreation(folder, host);
            EnsureBuilt(folder, key, descending);
            var e = Event.current;
            var lay = Measure(listRect);

            HandlePing(lay, host);

            switch (e.type)
            {
                case EventType.ScrollWheel:
                    if (listRect.Contains(e.mousePosition))
                    {
                        // Vertical wheel scrolls down the columns; with Shift (or when there is
                        // nothing to scroll vertically) it walks across the type columns instead.
                        if (e.shift || !lay.NeedV) _scroll.x += e.delta.y * RowHeight;
                        else _scroll.y += e.delta.y * RowHeight;
                        ClampScroll(lay);
                        host.Repaint?.Invoke();
                        e.Use();
                    }
                    break;

                case EventType.MouseMove:
                    var hover = lay.Viewport.Contains(e.mousePosition)
                        ? HitTest(e.mousePosition, lay, out _) : null;
                    if (hover != _hoverPath) { _hoverPath = hover; host.Repaint?.Invoke(); }
                    break;

                case EventType.KeyDown:
                    HandleKeyDown(e, lay, host);
                    break;

                case EventType.MouseDown:
                    // A click anywhere commits an in-progress rename first (as the stock
                    // browser does), then the click proceeds to select/open as usual.
                    if (_renamePath != null) { CommitRename(); EnsureBuilt(folder, key, descending); }
                    if (lay.NeedH && lay.HBar.Contains(e.mousePosition)) { BeginThumbDrag(lay, true, e); e.Use(); break; }
                    if (lay.NeedV && lay.VBar.Contains(e.mousePosition)) { BeginThumbDrag(lay, false, e); e.Use(); break; }
                    if (!lay.Viewport.Contains(e.mousePosition)) break;
                    HandleListMouseDown(e, lay, host);
                    break;

                case EventType.MouseDrag:
                    if (_thumbDrag != 0) { DragThumb(lay, e); host.Repaint?.Invoke(); e.Use(); }
                    else if (_maybeDragging && _pressedPath != null)
                    {
                        if ((e.mousePosition - _pressPos).magnitude > DragThreshold)
                        {
                            StartAssetDrag(_pressedPath);
                            _maybeDragging = false;
                            _pressedPath = null;
                            _clickSelectPending = false; // a drag, not a click — keep the selection
                        }
                        // Consume even below the threshold so the covered browser stays inert.
                        e.Use();
                    }
                    break;

                case EventType.DragUpdated:
                case EventType.DragPerform:
                    // The list pane is covered by this view, but the browser underneath would
                    // still react to a drag passing over it — selecting/pinging whatever sits
                    // at that spot in its own (hidden, differently ordered) layout, which
                    // switches the Inspector to the wrong object. Swallow the drag here so only
                    // real drop targets elsewhere (scene, object fields, the folder tree) act.
                    //
                    // Drops over a specific folder entry (opt-in) target that folder; otherwise
                    // the shown folder itself, the natural target after a spring-load brought us
                    // to a sibling tab. Scene GameObjects in the drag become brand-new prefabs
                    // there (Copy mode), matching the stock browser's Hierarchy → Project drop.
                    if (listRect.Contains(e.mousePosition))
                    {
                        string hoveredFolder = null;
                        if (TabstepSettings.ColumnViewFolderDrop)
                        {
                            var hit = HitTest(e.mousePosition, lay, out bool isFolder);
                            if (isFolder) hoveredFolder = hit;
                        }
                        string dropTo = hoveredFolder ?? folder;
                        var sceneRoots = CollectSceneRootsForPrefab();
                        var draggedPaths = CollectDraggedAssetPaths();
                        bool willCreatePrefabs = sceneRoots.Count > 0
                            && !string.IsNullOrEmpty(dropTo)
                            && AssetDatabase.IsValidFolder(dropTo);
                        bool willMoveAssets = !willCreatePrefabs
                            && HasMoveableAssetInto(dropTo, draggedPaths);
                        if (willCreatePrefabs || willMoveAssets)
                        {
                            DragAndDrop.visualMode = willCreatePrefabs
                                ? DragAndDropVisualMode.Copy
                                : DragAndDropVisualMode.Move;
                            if (e.type == EventType.DragPerform)
                            {
                                DragAndDrop.AcceptDrag();
                                if (willCreatePrefabs) CreatePrefabsInto(dropTo, sceneRoots);
                                else MoveAssetsInto(dropTo, draggedPaths);
                                _dropFolder = null;
                            }
                            else
                            {
                                // Highlight only the explicit folder row, never the bare
                                // viewport — the latter would feel like the whole pane is
                                // selected as a target.
                                _dropFolder = hoveredFolder;
                            }
                        }
                        else
                        {
                            DragAndDrop.visualMode = DragAndDropVisualMode.None;
                            _dropFolder = null;
                        }
                        host.Repaint?.Invoke();
                        e.Use();
                    }
                    break;

                case EventType.DragExited:
                    _dropFolder = null;
                    break;

                case EventType.MouseUp:
                    if (_thumbDrag != 0) { _thumbDrag = 0; e.Use(); }
                    else if (_clickSelectPending)
                    {
                        // A plain click that never became a drag: select now (this is the
                        // point the Inspector is allowed to switch).
                        Selection.activeObject = _pressedPath != null
                            ? AssetDatabase.LoadMainAssetAtPath(_pressedPath)
                            : null;
                        if (_pressedPath != null) _selectionAnchor = _pressedPath;
                        host.Repaint?.Invoke();
                        e.Use();
                    }
                    _clickSelectPending = false;
                    _maybeDragging = false;
                    _pressedPath = null;
                    break;
            }
        }

        void HandleListMouseDown(Event e, Layout lay, Host host)
        {
            var path = HitTest(e.mousePosition, lay, out bool isFolder);

            if (e.button == 0)
            {
                if (e.clickCount == 2)
                {
                    _maybeDragging = false;
                    _pressedPath = null;
                    _clickSelectPending = false;
                    if (path != null) OpenItem(path, isFolder, host);
                }
                else if (e.shift || e.control || e.command)
                {
                    // A deliberate range/toggle selection — apply it on press.
                    if (path != null) ApplyClickSelection(path, e);
                    _pressedPath = path;
                    _pressPos = e.mousePosition;
                    _maybeDragging = path != null;
                    _clickSelectPending = false;
                }
                else
                {
                    // Plain press: defer the selection to release. Grabbing an item to drag
                    // it must not switch the Inspector — only a click that does not turn into
                    // a drag selects, handled on mouse up.
                    _pressedPath = path;
                    _pressPos = e.mousePosition;
                    _maybeDragging = path != null;
                    _clickSelectPending = true;
                }
                host.Repaint?.Invoke();
                e.Use();
            }
            else if (e.button == 1)
            {
                if (path != null && !IsSelected(path))
                {
                    Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(path);
                    _selectionAnchor = path;
                }
                host.MarkBrowserInteracted?.Invoke();
                EditorUtility.DisplayPopupMenu(new Rect(e.mousePosition.x, e.mousePosition.y, 0, 0), "Assets/", null);
                e.Use();
            }
            else if (e.button == 2)
            {
                if (path != null && isFolder) host.OpenFolderInNewTab?.Invoke(path);
                e.Use();
            }
        }

        // ---- draw pass (runs AFTER the embedded browser paints) ------------------

        public void Draw(Rect listRect, string folder, AssetSortKey key, bool descending)
        {
            EnsureBuilt(folder, key, descending);
            var lay = Measure(listRect);

            if (Event.current.type == EventType.Repaint)
            {
                var ping = PingTracker.Active(out float pingProgress);
                _pingPath = ping;
                _pingAlpha = ping != null ? PingAlpha(pingProgress) : 0f;

                EditorGUI.DrawRect(listRect, BgColor);

                if (_columns.Count == 0)
                {
                    GUI.Label(listRect, "This folder is empty.", EmptyStyle);
                }
                else
                {
                    var selected = SelectedPaths();
                    var offset = new Vector2(-_scroll.x, -_scroll.y);
                    GUI.BeginClip(lay.Viewport);
                    for (int ci = 0; ci < _columns.Count; ci++)
                    {
                        var col = _columns[ci];
                        float colX = Padding + ci * (ColumnWidth + ColumnGap) + offset.x;
                        if (colX > lay.Viewport.width || colX + ColumnWidth < 0) continue; // off-screen column

                        DrawHeader(new Rect(colX, offset.y, ColumnWidth, HeaderHeight), col);
                        for (int ii = 0; ii < col.Items.Count; ii++)
                        {
                            float y = HeaderHeight + Padding + ii * RowHeight + offset.y;
                            if (y >= lay.Viewport.height || y + RowHeight <= 0) continue; // off-screen row
                            DrawRow(new Rect(colX, y, ColumnWidth, RowHeight), col.Items[ii], selected);
                        }
                    }
                    GUI.EndClip();

                    if (lay.NeedH) DrawScrollbar(lay, true);
                    if (lay.NeedV) DrawScrollbar(lay, false);
                }
            }

            // The inline rename field is an interactive control, so it must run on every
            // event pass (not just Repaint), and it paints after the columns so it lands on
            // top of the row it edits.
            if (_renamePath != null) DrawRenameOverlay(lay);
        }

        void DrawHeader(Rect r, Column col)
        {
            EditorGUI.DrawRect(r, HeaderColor);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), DividerColor);
            GUI.Label(new Rect(r.x + 5, r.y, r.width - 8, r.height),
                $"{col.Label}  ({col.Items.Count})", HeaderStyle);
        }

        void DrawRow(Rect r, Item item, HashSet<string> selected)
        {
            bool isSelected = selected.Contains(item.Path);
            bool isPing = _pingAlpha > 0.001f && item.Path == _pingPath;
            if (item.Path == _dropFolder)
            {
                EditorGUI.DrawRect(r, DropColor);
                DrawBorder(r, DropBorderColor);
            }
            else if (isSelected) EditorGUI.DrawRect(r, SelColor);
            else if (item.Path == _hoverPath) EditorGUI.DrawRect(r, HoverColor);

            // Ping flash tint, under the icon/label so they stay readable.
            if (isPing) EditorGUI.DrawRect(r, new Color(PingColor.r, PingColor.g, PingColor.b, _pingAlpha * 0.5f));

            // A brand-new asset has no AssetDatabase entry yet; fall back to the
            // preview icon Unity passed with BeginPreimportedNameEditing.
            Texture icon = AssetDatabase.GetCachedIcon(item.Path);
            if (icon == null && _creation != null && item.Path == _creation.PathName)
                icon = _creation.Icon;
            var iconRect = new Rect(r.x + 4, r.y + (r.height - IconSize) / 2, IconSize, IconSize);
            if (icon != null) GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

            // The row being renamed has its label replaced by the rename text field overlay.
            if (item.Path == _renamePath) return;

            var labelRect = new Rect(iconRect.xMax + 4, r.y, r.xMax - iconRect.xMax - 6, r.height);
            GUI.Label(labelRect, item.Name, isSelected ? RowSelStyle : RowStyle);

            if (isPing) DrawBorder(r, new Color(PingColor.r, PingColor.g, PingColor.b, _pingAlpha));
        }

        static HashSet<string> SelectedPaths()
        {
            var set = new HashSet<string>();
            foreach (var o in Selection.objects)
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(p)) set.Add(p);
            }
            return set;
        }

        void DrawScrollbar(Layout lay, bool horizontal)
        {
            var (track, thumb) = ThumbRects(lay, horizontal);
            EditorGUI.DrawRect(track, TrackColor);
            EditorGUI.DrawRect(thumb, ThumbColor);
        }

        // ---- inline rename (F2) --------------------------------------------------

        void HandleKeyDown(Event e, Layout lay, Host host)
        {
            if (_renamePath != null)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    CommitRename();
                    host.Repaint?.Invoke();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    EndRename();
                    host.Repaint?.Invoke();
                    e.Use();
                }
                // Other keys (the typing itself) fall through to the text field in Draw.
                return;
            }

            if (e.keyCode == KeyCode.F2)
            {
                if (BeginRename())
                {
                    if (FindItem(_renamePath, out int ci, out int ii)) ScrollItemIntoView(ci, ii, lay);
                    host.Repaint?.Invoke();
                    e.Use();
                }
                return;
            }

            // Leave keys to an active text field elsewhere (path bar, search) — never steal
            // them. The rename field is handled above, before this point.
            if (EditorGUIUtility.editingTextField) return;

            // Enter opens the active selection (folder navigates, asset opens) — the stock
            // browser's behaviour, which never reached the covered list. Always consume it so
            // the hidden browser underneath never also reacts (the core invariant of this view).
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                OpenActiveSelection(host);
                e.Use();
                return;
            }

            // Delete (Cmd+Backspace on macOS) sends the selected assets to the trash, with the
            // same confirmation the stock browser shows. Consumed unconditionally for the same
            // reason as Enter above.
            if (e.keyCode == KeyCode.Delete || (e.command && e.keyCode == KeyCode.Backspace))
            {
                DeleteSelection(host);
                e.Use();
                return;
            }

            // Arrow-key selection — Up/Down walk the current type column, Left/Right
            // step across to the previous/next column. Shift extends the selection
            // from the anchor, matching plain/Shift-click. Always consume them so the
            // hidden browser underneath never also acts on the same key.
            if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow ||
                e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow ||
                e.keyCode == KeyCode.Home || e.keyCode == KeyCode.End)
            {
                if (MoveSelection(e, lay, host)) e.Use();
            }
        }

        // ---- arrow-key selection -----------------------------------------------

        /// <summary>
        /// Moves the active selection one step in the direction of <paramref name="e"/>,
        /// or jumps to the first/last item on Home/End. Returns false when there is
        /// nothing to step through (empty folder) so the key is left for other handlers.
        /// </summary>
        bool MoveSelection(Event e, Layout lay, Host host)
        {
            if (_columns.Count == 0) return false;

            // Where the cursor currently sits in the grid. With no selection shown
            // in this view (nothing active, or active item lives outside the
            // columns) the first arrow press just lands on (0,0), like the stock
            // browser's first Down/Right into an unfocused list.
            int ci = -1, ii = -1;
            var activePath = AssetDatabase.GetAssetPath(Selection.activeObject);
            bool hasAnchor = !string.IsNullOrEmpty(activePath) && FindItem(activePath, out ci, out ii);
            int newCi, newIi;
            if (!hasAnchor)
            {
                // No prior selection here — any arrow lands on the first item.
                newCi = 0;
                newIi = 0;
            }
            else
            {
                newCi = ci;
                newIi = ii;
                switch (e.keyCode)
                {
                    case KeyCode.UpArrow:
                        newIi = Mathf.Max(0, ii - 1);
                        break;
                    case KeyCode.DownArrow:
                        newIi = Mathf.Min(_columns[ci].Items.Count - 1, ii + 1);
                        break;
                    case KeyCode.LeftArrow:
                        newCi = Mathf.Max(0, ci - 1);
                        // The previous column may be shorter — clamp so we land on a real row.
                        newIi = Mathf.Min(ii, _columns[newCi].Items.Count - 1);
                        break;
                    case KeyCode.RightArrow:
                        newCi = Mathf.Min(_columns.Count - 1, ci + 1);
                        newIi = Mathf.Min(ii, _columns[newCi].Items.Count - 1);
                        break;
                    case KeyCode.Home:
                        newCi = 0;
                        newIi = 0;
                        break;
                    case KeyCode.End:
                        newCi = _columns.Count - 1;
                        newIi = _columns[newCi].Items.Count - 1;
                        break;
                }
            }
            if (newCi == ci && newIi == ii) return true; // already there — consume but no-op

            string newPath = _columns[newCi].Items[newIi].Path;
            var obj = AssetDatabase.LoadMainAssetAtPath(newPath);
            if (obj == null) return true; // phantom row or missing asset — swallow but no selection change

            if (e.shift)
            {
                // Extend from the anchor (set on the last plain selection) to the new
                // item, matching Shift+click. SelectRange leaves the anchor in place
                // so the next Shift+arrow stretches further in either direction.
                SelectRange(newPath, additive: false);
                Selection.activeObject = obj;
            }
            else
            {
                Selection.activeObject = obj;
                _selectionAnchor = newPath;
            }
            ScrollItemIntoView(newCi, newIi, lay);
            host.Repaint?.Invoke();
            return true;
        }

        /// <summary>
        /// Opens the active selection like a double-click, but only when it is actually shown in
        /// this view (mirroring <see cref="BeginRename"/>) so Enter never acts on an off-view item.
        /// </summary>
        bool OpenActiveSelection(Host host)
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path) || !FindItem(path, out _, out _)) return false;
            OpenItem(path, AssetDatabase.IsValidFolder(path), host);
            return true;
        }

        /// <summary>
        /// Sends the selected assets to the trash after a confirmation, mirroring the stock
        /// browser's Delete. Returns false (key not consumed) only when nothing is selected;
        /// returns true once the prompt is shown, even if the user cancels.
        /// </summary>
        bool DeleteSelection(Host host)
        {
            var paths = new List<string>();
            foreach (var o in Selection.objects)
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            if (paths.Count == 0) return false;

            string message = paths.Count == 1
                ? $"\"{Path.GetFileName(paths[0])}\" will be moved to the trash.\nYou can restore it from there."
                : $"{paths.Count} assets will be moved to the trash.\nYou can restore them from there.";
            if (!EditorUtility.DisplayDialog("Delete selected assets?", message, "Delete", "Cancel"))
                return true; // prompt dismissed — still consume the key so the browser stays inert

            var failed = new List<string>();
            if (!AssetDatabase.MoveAssetsToTrash(paths.ToArray(), failed) || failed.Count > 0)
                Debug.LogWarning($"Tabstep: some assets could not be deleted: {string.Join(", ", failed)}");
            Selection.objects = System.Array.Empty<Object>();
            _selectionAnchor = null;
            ProjectVersion++; // rebuild without resetting the scroll (MarkDirty would)
            host.Repaint?.Invoke();
            return true;
        }

        /// <summary>
        /// Picks up a pending Assets/Create/... request and switches the column view
        /// into "naming a new asset" mode. The request was deposited by the Harmony
        /// patch in <see cref="ProjectBrowserPatcher"/>; the phantom row is folded
        /// into the columns by <see cref="EnsureBuilt"/> on the next rebuild.
        /// </summary>
        void ConsumePendingCreation(string folder, Host host)
        {
            // An in-progress create still owns the rename overlay — leave it alone.
            if (_creation != null) return;
            if (host.TakePendingCreation == null) return;
            var request = host.TakePendingCreation();
            if (request == null) return;

            // Discard a request meant for a folder we no longer show (the user
            // navigated away between the Create click and this event pass): if we
            // kept it, the phantom would never render and the user would be stuck
            // with an invisible rename overlay running EndAction on focus loss.
            if (string.IsNullOrEmpty(folder) || ParentFolder(request.PathName) != folder)
            {
                request.EndAction?.Cancelled(request.InstanceID, request.PathName, request.ResourceFile);
                return;
            }

            // A user-driven rename in progress is dropped — the Create request wins,
            // the same way the stock browser swaps overlays when Create arrives.
            if (_renamePath != null) EndRename();

            _creation = request;
            _renamePath = request.PathName;
            _renameText = Path.GetFileNameWithoutExtension(request.PathName);
            _renameFocusPending = true;
            host.Repaint?.Invoke();
        }

        /// <summary>
        /// Starts renaming the selected item — the active object when it is shown here,
        /// otherwise the first selected item in reading order. Returns false when nothing
        /// renameable is selected in this view.
        /// </summary>
        bool BeginRename()
        {
            string target = null;
            var active = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(active) && FindItem(active, out _, out _))
            {
                target = active;
            }
            else
            {
                var selected = SelectedPaths();
                foreach (var path in FlattenedPaths())
                    if (selected.Contains(path)) { target = path; break; }
            }
            if (target == null) return false;

            _renamePath = target;
            _renameText = AssetDatabase.IsValidFolder(target)
                ? Path.GetFileName(target)
                : Path.GetFileNameWithoutExtension(target);
            _renameFocusPending = true;
            return true;
        }

        /// <summary>
        /// Draws the rename text field over the edited row and drives focus. The field paints
        /// on top of the columns; the underlying row's label is suppressed in <see cref="DrawRow"/>.
        /// </summary>
        void DrawRenameOverlay(Layout lay)
        {
            if (!FindItem(_renamePath, out int ci, out int ii)) { EndRename(); return; }

            float colX = lay.Viewport.x + Padding + ci * (ColumnWidth + ColumnGap) - _scroll.x;
            float y = lay.Viewport.y + HeaderHeight + Padding + ii * RowHeight - _scroll.y;
            var rowRect = new Rect(colX, y, ColumnWidth, RowHeight);
            if (!lay.Viewport.Overlaps(rowRect)) return; // scrolled out of view this frame

            var fieldRect = new Rect(rowRect.x + IconSize + 8, rowRect.y + 1,
                Mathf.Max(20f, rowRect.width - IconSize - 12), rowRect.height - 2);

            GUI.SetNextControlName(RenameControlName);
            _renameText = GUI.TextField(fieldRect, _renameText ?? string.Empty, RenameStyle);

            if (_renameFocusPending)
            {
                EditorGUI.FocusTextInControl(RenameControlName); // focuses and selects the name
                if (GUI.GetNameOfFocusedControl() == RenameControlName) _renameFocusPending = false;
            }
            else if (Event.current.type == EventType.Repaint &&
                     GUI.GetNameOfFocusedControl() != RenameControlName)
            {
                // Focus moved elsewhere (e.g. another window) — commit, like the stock browser.
                CommitRename();
            }
        }

        /// <summary>Applies the edited name via the asset database and reselects the result.</summary>
        void CommitRename()
        {
            string path = _renamePath;
            string text = (_renameText ?? string.Empty).Trim();
            var creation = _creation;
            // Clear our rename state first so EndRename (called below) does not also
            // try to cancel the creation we are about to commit.
            _creation = null;
            EndRename();
            if (string.IsNullOrEmpty(path)) return;

            // Naming a brand-new asset (Assets/Create/...) — hand the typed name to
            // the captured EndNameEditAction so Unity's own creation pipeline runs.
            if (creation != null)
            {
                string fallback = Path.GetFileNameWithoutExtension(creation.PathName);
                if (string.IsNullOrEmpty(text)) text = fallback;
                string parent = ParentFolder(creation.PathName);
                string ext = Path.GetExtension(creation.PathName);
                string finalPath = AssetDatabase.GenerateUniqueAssetPath(
                    (parent.Length == 0 ? string.Empty : parent + "/") + text + ext);
                try
                {
                    creation.EndAction.Action(creation.InstanceID, finalPath, creation.ResourceFile);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Tabstep: could not create \"{finalPath}\": {e}");
                }
                // Most EndNameEditActions select the new asset themselves (via
                // ProjectWindowUtil.ShowCreatedAsset); fall back here for the ones
                // that do not, so the just-named asset is always highlighted.
                var created = AssetDatabase.LoadMainAssetAtPath(finalPath);
                if (created != null)
                {
                    Selection.activeObject = created;
                    _selectionAnchor = finalPath;
                }
                ProjectVersion++; // the phantom is gone; the real asset (if created) takes its slot
                return;
            }

            bool isFolder = AssetDatabase.IsValidFolder(path);
            string currentName = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(text) || text == currentName) return;

            string error = AssetDatabase.RenameAsset(path, text); // keeps the extension itself
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning($"Tabstep: could not rename \"{path}\": {error}");
                return;
            }

            // Reselect the asset at its new path so the selection follows the rename.
            string renamedParent = ParentFolder(path);
            string renamedExt = isFolder ? string.Empty : Path.GetExtension(path);
            string newPath = (renamedParent.Length == 0 ? string.Empty : renamedParent + "/") + text + renamedExt;
            var obj = AssetDatabase.LoadMainAssetAtPath(newPath);
            if (obj != null) { Selection.activeObject = obj; _selectionAnchor = newPath; }
            // Force a rebuild for the new name without resetting the scroll (which MarkDirty,
            // by clearing the built folder, would do — EnsureBuilt resets scroll on a folder
            // change). projectChanged fires too, but not necessarily within this same event.
            ProjectVersion++;
        }

        /// <summary>Ends the rename without applying it and releases the text field focus.</summary>
        void EndRename()
        {
            // If a brand-new asset was being named, tell Unity to discard the
            // would-be creation (delete its temporary preview, free its instance id).
            if (_creation != null)
            {
                var c = _creation;
                _creation = null;
                try
                {
                    c.EndAction?.Cancelled(c.InstanceID, c.PathName, c.ResourceFile);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Tabstep: cancelling \"{c.PathName}\" threw: {e}");
                }
                ProjectVersion++; // drop the phantom from the columns
            }
            _renamePath = null;
            _renameText = null;
            _renameFocusPending = false;
            if (GUI.GetNameOfFocusedControl() == RenameControlName) GUI.FocusControl(null);
            // IMGUI keeps `editingTextField` set after the text field's control id
            // is gone; if we leave it, HandleKeyDown's "leave keys to a text field"
            // early-out swallows every Up/Down/Left/Right until the user clicks
            // somewhere. Clear it explicitly so arrow navigation resumes the moment
            // the rename ends.
            EditorGUIUtility.editingTextField = false;
        }

        // ---- geometry ------------------------------------------------------------

        struct Layout
        {
            public Rect Viewport;       // area the columns are clipped to (excludes scrollbars)
            public bool NeedH, NeedV;
            public Rect HBar, VBar;     // scrollbar track rects, in window coordinates
            public float ContentW, ContentH;
            public float MaxX, MaxY;
        }

        Layout Measure(Rect listRect)
        {
            float contentW = _columns.Count * (ColumnWidth + ColumnGap) + Padding;
            int maxRows = 0;
            foreach (var c in _columns) maxRows = Mathf.Max(maxRows, c.Items.Count);
            float contentH = HeaderHeight + Padding + maxRows * RowHeight + Padding;

            bool needH = contentW > listRect.width;
            bool needV = contentH > listRect.height - (needH ? ScrollbarThickness : 0);
            if (needV) needH = contentW > listRect.width - ScrollbarThickness;

            float viewW = listRect.width - (needV ? ScrollbarThickness : 0);
            float viewH = listRect.height - (needH ? ScrollbarThickness : 0);

            var lay = new Layout
            {
                NeedH = needH,
                NeedV = needV,
                ContentW = contentW,
                ContentH = contentH,
                Viewport = new Rect(listRect.x, listRect.y, viewW, viewH),
                MaxX = Mathf.Max(0, contentW - viewW),
                MaxY = Mathf.Max(0, contentH - viewH),
                HBar = new Rect(listRect.x, listRect.yMax - ScrollbarThickness, viewW, ScrollbarThickness),
                VBar = new Rect(listRect.xMax - ScrollbarThickness, listRect.y, ScrollbarThickness, viewH),
            };
            ClampScroll(lay);
            return lay;
        }

        void ClampScroll(Layout lay)
        {
            _scroll.x = Mathf.Clamp(_scroll.x, 0, lay.MaxX);
            _scroll.y = Mathf.Clamp(_scroll.y, 0, lay.MaxY);
        }

        // ---- ping flash ----------------------------------------------------------

        /// <summary>
        /// While an asset ping is alive, keep repainting (so the flash animates) and, once per
        /// ping, scroll the pinged item into view — matching the stock list's behaviour.
        /// </summary>
        void HandlePing(Layout lay, Host host)
        {
            var path = PingTracker.Active(out _);
            if (path == null) return;
            host.Repaint?.Invoke();
            if (PingTracker.StartTime != _pingScrolled && FindItem(path, out int ci, out int ii))
            {
                _pingScrolled = PingTracker.StartTime;
                ScrollItemIntoView(ci, ii, lay);
            }
        }

        bool FindItem(string path, out int ci, out int ii)
        {
            for (ci = 0; ci < _columns.Count; ci++)
            {
                var items = _columns[ci].Items;
                for (ii = 0; ii < items.Count; ii++)
                    if (items[ii].Path == path) return true;
            }
            ci = -1;
            ii = -1;
            return false;
        }

        void ScrollItemIntoView(int ci, int ii, Layout lay)
        {
            float itemX = Padding + ci * (ColumnWidth + ColumnGap);
            float itemY = HeaderHeight + Padding + ii * RowHeight;
            if (itemX < _scroll.x) _scroll.x = itemX;
            else if (itemX + ColumnWidth > _scroll.x + lay.Viewport.width)
                _scroll.x = itemX + ColumnWidth - lay.Viewport.width;
            if (itemY < _scroll.y) _scroll.y = itemY;
            else if (itemY + RowHeight > _scroll.y + lay.Viewport.height)
                _scroll.y = itemY + RowHeight - lay.Viewport.height;
            ClampScroll(lay);
        }

        static float PingAlpha(float progress)
        {
            float a = Mathf.Clamp01(1f - progress);          // fade out over the ping's life
            a *= 0.6f + 0.4f * Mathf.Cos(progress * 6.2831853f * 1.5f); // a couple of pulses
            return Mathf.Clamp01(a);
        }

        /// <summary>Resolves the path under <paramref name="mouse"/> (window coords), or null.</summary>
        string HitTest(Vector2 mouse, Layout lay, out bool isFolder)
        {
            isFolder = false;
            var content = (mouse - lay.Viewport.position) + _scroll;
            if (content.x < Padding || content.y < HeaderHeight + Padding) return null;

            float stride = ColumnWidth + ColumnGap;
            int ci = Mathf.FloorToInt((content.x - Padding) / stride);
            if (ci < 0 || ci >= _columns.Count) return null;
            if ((content.x - Padding) - ci * stride > ColumnWidth) return null; // in the gap between columns

            int ii = Mathf.FloorToInt((content.y - HeaderHeight - Padding) / RowHeight);
            var col = _columns[ci];
            if (ii < 0 || ii >= col.Items.Count) return null;

            var path = col.Items[ii].Path;
            isFolder = AssetDatabase.IsValidFolder(path);
            return path;
        }

        (Rect track, Rect thumb) ThumbRects(Layout lay, bool horizontal)
        {
            Rect track = horizontal ? lay.HBar : lay.VBar;
            float content = horizontal ? lay.ContentW : lay.ContentH;
            float view = horizontal ? lay.Viewport.width : lay.Viewport.height;
            float trackLen = horizontal ? track.width : track.height;
            float frac = content > 0 ? Mathf.Clamp01(view / content) : 1f;
            float thumbLen = Mathf.Max(MinThumb, trackLen * frac);
            float maxScroll = horizontal ? lay.MaxX : lay.MaxY;
            float t = maxScroll > 0 ? (horizontal ? _scroll.x : _scroll.y) / maxScroll : 0f;
            float travel = trackLen - thumbLen;
            Rect thumb = horizontal
                ? new Rect(track.x + t * travel, track.y + 2, thumbLen, track.height - 4)
                : new Rect(track.x + 2, track.y + t * travel, track.width - 4, thumbLen);
            return (track, thumb);
        }

        void BeginThumbDrag(Layout lay, bool horizontal, Event e)
        {
            _thumbDrag = horizontal ? 1 : 2;
            var (_, thumb) = ThumbRects(lay, horizontal);
            float along = horizontal ? e.mousePosition.x : e.mousePosition.y;
            float thumbStart = horizontal ? thumb.x : thumb.y;
            float thumbEnd = horizontal ? thumb.xMax : thumb.yMax;
            if (along < thumbStart || along > thumbEnd)
            {
                // Clicked the track outside the thumb — jump so the thumb centres on the cursor.
                _thumbGrabOffset = (horizontal ? thumb.width : thumb.height) / 2f;
                DragThumbTo(lay, horizontal, along);
            }
            else
            {
                _thumbGrabOffset = along - thumbStart;
            }
        }

        void DragThumb(Layout lay, Event e)
        {
            bool horizontal = _thumbDrag == 1;
            DragThumbTo(lay, horizontal, horizontal ? e.mousePosition.x : e.mousePosition.y);
        }

        void DragThumbTo(Layout lay, bool horizontal, float along)
        {
            var (track, thumb) = ThumbRects(lay, horizontal);
            float trackStart = horizontal ? track.x : track.y;
            float trackLen = horizontal ? track.width : track.height;
            float thumbLen = horizontal ? thumb.width : thumb.height;
            float travel = trackLen - thumbLen;
            float t = travel > 0 ? Mathf.Clamp01((along - _thumbGrabOffset - trackStart) / travel) : 0f;
            if (horizontal) _scroll.x = t * lay.MaxX;
            else _scroll.y = t * lay.MaxY;
        }

        // ---- selection / open / drag ---------------------------------------------

        static bool IsSelected(string path)
        {
            foreach (var o in Selection.objects)
                if (AssetDatabase.GetAssetPath(o) == path) return true;
            return false;
        }

        void ApplyClickSelection(string path, Event e)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null) return;
            bool additive = e.control || e.command;

            // Shift extends a range from the anchor; the anchor itself stays put so the
            // range can be re-stretched, exactly like Explorer and Unity's own list.
            if (e.shift)
            {
                SelectRange(path, additive);
                return;
            }

            if (additive)
            {
                var list = new List<Object>(Selection.objects);
                int idx = list.IndexOf(obj);
                if (idx >= 0) list.RemoveAt(idx);
                else list.Add(obj);
                Selection.objects = list.ToArray();
            }
            else
            {
                Selection.activeObject = obj;
            }
            _selectionAnchor = path;
        }

        /// <summary>
        /// Selects every item between the anchor and <paramref name="targetPath"/> in the
        /// visual reading order (down each column, then across to the next). With
        /// <paramref name="additive"/> (Shift+Ctrl) the range is added to the current
        /// selection instead of replacing it.
        /// </summary>
        void SelectRange(string targetPath, bool additive)
        {
            var order = FlattenedPaths();
            int ti = order.IndexOf(targetPath);
            if (ti < 0) return;

            string anchor = _selectionAnchor;
            if (anchor == null)
            {
                var active = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (!string.IsNullOrEmpty(active) && order.Contains(active)) anchor = active;
            }
            int ai = anchor != null ? order.IndexOf(anchor) : -1;
            if (ai < 0) { ai = ti; _selectionAnchor = targetPath; }

            int lo = Mathf.Min(ai, ti), hi = Mathf.Max(ai, ti);
            var objs = new List<Object>();
            if (additive)
                objs.AddRange(Selection.objects);
            for (int i = lo; i <= hi; i++)
            {
                var o = AssetDatabase.LoadMainAssetAtPath(order[i]);
                if (o != null && !objs.Contains(o)) objs.Add(o);
            }
            Selection.objects = objs.ToArray();
        }

        /// <summary>Every item path in the visual reading order: down each column, then across.</summary>
        List<string> FlattenedPaths()
        {
            var paths = new List<string>();
            foreach (var col in _columns)
                foreach (var item in col.Items)
                    paths.Add(item.Path);
            return paths;
        }

        static void OpenItem(string path, bool isFolder, Host host)
        {
            if (isFolder) { host.OpenFolder?.Invoke(path); return; }
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj != null) AssetDatabase.OpenAsset(obj);
        }

        static void StartAssetDrag(string path)
        {
            var paths = new List<string>();
            var objs = new List<Object>();
            if (IsSelected(path))
            {
                foreach (var o in Selection.objects)
                {
                    var p = AssetDatabase.GetAssetPath(o);
                    if (!string.IsNullOrEmpty(p)) { paths.Add(p); objs.Add(o); }
                }
            }
            if (objs.Count == 0)
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj != null) { paths.Add(path); objs.Add(obj); }
            }
            if (objs.Count == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objs.ToArray();
            DragAndDrop.paths = paths.ToArray();
            DragAndDrop.StartDrag(objs.Count == 1 ? objs[0].name : objs.Count + " Assets");
        }

        /// <summary>
        /// True when at least one path in <paramref name="paths"/> would actually move into
        /// <paramref name="folder"/> — i.e. it exists outside the folder and is not the
        /// folder itself. Used so the cursor only shows "Move" while a real drop would
        /// happen; an empty viewport drop and a same-folder drop both leave it as None.
        /// </summary>
        static bool HasMoveableAssetInto(string folder, List<string> paths)
        {
            if (string.IsNullOrEmpty(folder) || paths == null || paths.Count == 0) return false;
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (path == folder || ParentFolder(path) == folder) continue;
                if (AssetDatabase.IsValidFolder(path) &&
                    folder.StartsWith(path + "/", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>Moves <paramref name="paths"/> into <paramref name="folder"/>, skipping no-ops.</summary>
        static void MoveAssetsInto(string folder, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrEmpty(p) || ParentFolder(p) == folder) continue;
                    // Never move a folder into itself or one of its own descendants.
                    if (AssetDatabase.IsValidFolder(p) &&
                        (folder == p || folder.StartsWith(p + "/", StringComparison.Ordinal)))
                        continue;
                    var dest = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(p));
                    AssetDatabase.MoveAsset(p, dest);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Asset paths of the current drag, unioned from <see cref="DragAndDrop.paths"/>
        /// and <see cref="DragAndDrop.objectReferences"/> — the stock browser's tree pane
        /// only populates the latter, so reading just paths missed tree drags.
        /// </summary>
        static List<string> CollectDraggedAssetPaths()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var raw in DragAndDrop.paths)
            {
                if (string.IsNullOrEmpty(raw) || !seen.Add(raw)) continue;
                result.Add(raw);
            }
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj == null) continue;
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                result.Add(path);
            }
            return result;
        }

        /// <summary>
        /// Non-persistent <see cref="GameObject"/>s in the drag — scene roots eligible
        /// to be saved out as brand-new prefab assets. Returns an empty list when the
        /// drag carries only project assets.
        /// </summary>
        static List<GameObject> CollectSceneRootsForPrefab()
        {
            var result = new List<GameObject>();
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj is GameObject go && !EditorUtility.IsPersistent(go))
                    result.Add(go);
            }
            return result;
        }

        /// <summary>
        /// Saves each <paramref name="sceneRoots"/> GameObject as a fresh prefab under
        /// <paramref name="folder"/> and reconnects the scene instance to it — what the
        /// stock browser does for a Hierarchy → Project drop.
        /// </summary>
        static void CreatePrefabsInto(string folder, List<GameObject> sceneRoots)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var go in sceneRoots)
                {
                    if (go == null) continue;
                    string baseName = string.IsNullOrEmpty(go.name) ? "GameObject" : go.name;
                    string dest = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + baseName + ".prefab");
                    try
                    {
                        PrefabUtility.SaveAsPrefabAssetAndConnect(go, dest, InteractionMode.UserAction);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Tabstep: could not save \"{go.name}\" as a prefab at \"{dest}\": {e}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }

        static string ParentFolder(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "" : path.Substring(0, slash);
        }

        // ---- building the columns ------------------------------------------------

        void EnsureBuilt(string folder, AssetSortKey key, bool descending)
        {
            string creationPath = CreationPathFor(folder);
            if (folder == _builtFolder && key == _builtKey && descending == _builtDescending &&
                _builtVersion == ProjectVersion && creationPath == _builtCreationPath)
                return;

            // Reset the scroll and range anchor when the shown folder changes.
            if (folder != _builtFolder)
            {
                _scroll = Vector2.zero;
                _selectionAnchor = null;
                if (_renamePath != null) EndRename(); // a pending rename does not survive navigation
            }

            _builtFolder = folder;
            _builtKey = key;
            _builtDescending = descending;
            _builtVersion = ProjectVersion;
            _builtCreationPath = creationPath;
            _columns.Clear();
            if (string.IsNullOrEmpty(folder)) return;

            var byLabel = new Dictionary<string, Column>();
            foreach (var path in EnumerateChildren(folder))
            {
                bool isFolder = AssetDatabase.IsValidFolder(path);
                string label = isFolder ? "Folders" : TypeLabel(path);
                if (!byLabel.TryGetValue(label, out var col))
                {
                    col = new Column { Label = label };
                    byLabel[label] = col;
                    _columns.Add(col);
                }
                var item = new Item { Path = path, Name = Path.GetFileNameWithoutExtension(path) };
                FillMeta(ref item, path, isFolder);
                col.Items.Add(item);
            }

            // A new asset being named (Assets/Create/...) does not exist on disk yet,
            // so EnumerateChildren skipped it. Add it as a phantom item under the
            // matching label so the rename overlay has a row to sit on.
            if (creationPath != null)
            {
                bool isFolder = CreationIsFolder(_creation);
                string label = isFolder ? "Folders" : TypeLabelForExtension(creationPath);
                if (!byLabel.TryGetValue(label, out var col))
                {
                    col = new Column { Label = label };
                    byLabel[label] = col;
                    _columns.Add(col);
                }
                var item = new Item
                {
                    Path = creationPath,
                    Name = Path.GetFileNameWithoutExtension(creationPath),
                };
                col.Items.Add(item);
            }

            // Folders column always first; the rest by type name (reversed when sorting by
            // Type descending). The chosen key then orders the items inside each column.
            _columns.Sort((a, b) =>
            {
                bool af = a.Label == "Folders", bf = b.Label == "Folders";
                if (af != bf) return af ? -1 : 1;
                int c = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                return key == AssetSortKey.Type && descending ? -c : c;
            });
            foreach (var col in _columns)
                col.Items.Sort((a, b) => CompareItems(a, b, key, descending));
        }

        /// <summary>
        /// The new-asset path the in-flight creation request would put under
        /// <paramref name="folder"/>, or null when there is no request or it belongs
        /// to a different folder.
        /// </summary>
        string CreationPathFor(string folder)
        {
            if (_creation == null || string.IsNullOrEmpty(folder)) return null;
            var path = _creation.PathName;
            if (string.IsNullOrEmpty(path) || ParentFolder(path) != folder) return null;
            return path;
        }

        /// <summary>True when the request is a folder create (no file extension).</summary>
        static bool CreationIsFolder(AssetCreationBridge.Request r)
        {
            if (r == null) return false;
            return string.IsNullOrEmpty(Path.GetExtension(r.PathName));
        }

        /// <summary>
        /// Label for an asset that does not exist on disk yet, derived from the
        /// file extension (so <see cref="TypeLabel"/>'s asset-database lookups,
        /// which would fail, are bypassed).
        /// </summary>
        static string TypeLabelForExtension(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return "Other";
            switch (ext.ToLowerInvariant())
            {
                case ".cs": return "Script";
                case ".unity": return "Scene";
                case ".prefab": return "Prefab";
                case ".asset": return "ScriptableObject";
                case ".mat": return "Material";
                case ".shader": return "Shader";
                case ".anim": return "AnimationClip";
                case ".controller": return "AnimatorController";
                case ".txt": return "Text";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga": return "Texture";
                default: return ext.TrimStart('.').ToUpperInvariant();
            }
        }

        static int CompareItems(Item a, Item b, AssetSortKey key, bool descending)
        {
            int c;
            switch (key)
            {
                case AssetSortKey.DateModified: c = a.DateTicks.CompareTo(b.DateTicks); break;
                case AssetSortKey.Size: c = a.Size.CompareTo(b.Size); break;
                default: c = 0; break; // Name and Type fall back to the natural name order
            }
            if (c == 0) c = NaturalCompare(a.Name, b.Name);
            // Type only reorders the columns, never the (same-type) items inside them.
            bool reverse = descending && key != AssetSortKey.Type;
            return reverse ? -c : c;
        }

        /// <summary>
        /// Explorer-style name ordering: case-insensitive, with runs of digits compared
        /// numerically so "item2" sorts before "item10".
        /// </summary>
        static int NaturalCompare(string a, string b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;
            int ia = 0, ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                char ca = a[ia], cb = b[ib];
                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    int sa = ia, sb = ib;
                    while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                    while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                    string na = a.Substring(sa, ia - sa).TrimStart('0');
                    string nb = b.Substring(sb, ib - sb).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length; // more digits = larger number
                    int cmp = string.CompareOrdinal(na, nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(ca).CompareTo(char.ToLowerInvariant(cb));
                    if (cmp != 0) return cmp;
                    ia++;
                    ib++;
                }
            }
            return (a.Length - ia) - (b.Length - ib); // shorter remaining string sorts first
        }

        static string TypeLabel(string path)
        {
            var t = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (t == null)
            {
                var ext = Path.GetExtension(path);
                return string.IsNullOrEmpty(ext) ? "Other" : ext.TrimStart('.').ToUpperInvariant();
            }
            switch (t.Name)
            {
                case "GameObject": return "Prefab";
                case "SceneAsset": return "Scene";
                case "MonoScript": return "Script";
                case "TextAsset": return "Text";
                case "Texture2D": return "Texture";
                case "AudioClip": return "Audio";
                case "DefaultAsset": return "Other";
                default: return ObjectNames.NicifyVariableName(t.Name);
            }
        }

        static IEnumerable<string> EnumerateChildren(string folder)
        {
            foreach (var sub in AssetDatabase.GetSubFolders(folder))
                yield return sub;

            string abs = AbsolutePath(folder);
            if (abs != null && Directory.Exists(abs))
            {
                foreach (var file in Directory.EnumerateFiles(abs))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(".")) continue; // hidden files are not assets
                    string rel = folder + "/" + name;
                    if (AssetDatabase.GetMainAssetTypeAtPath(rel) == null &&
                        AssetDatabase.LoadAssetAtPath<Object>(rel) == null)
                        continue; // skip files the asset database does not track
                    yield return rel;
                }
            }
            else
            {
                // Packages / virtual roots that are not directly on disk: use the asset index.
                foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(p) || AssetDatabase.IsValidFolder(p)) continue;
                    int slash = p.LastIndexOf('/');
                    if (slash <= 0 || p.Substring(0, slash) != folder) continue; // immediate children only
                    yield return p;
                }
            }
        }

        static void FillMeta(ref Item item, string path, bool isFolder)
        {
            string abs = AbsolutePath(path);
            try
            {
                if (!isFolder && abs != null && File.Exists(abs))
                {
                    var fi = new FileInfo(abs);
                    item.Size = fi.Length;
                    item.DateTicks = fi.LastWriteTimeUtc.Ticks;
                }
                else if (abs != null && Directory.Exists(abs))
                {
                    item.DateTicks = Directory.GetLastWriteTimeUtc(abs).Ticks;
                }
            }
            catch
            {
                // Date/size are best-effort; a missing file just sorts as zero.
            }
        }

        static string AbsolutePath(string projectPath)
        {
            if (projectPath == "Assets") return Application.dataPath;
            if (projectPath.StartsWith("Assets/", StringComparison.Ordinal))
                return Application.dataPath + projectPath.Substring("Assets".Length);
            try { return Path.GetFullPath(projectPath); }
            catch { return null; }
        }

        // ---- styles & colours ----------------------------------------------------

        static GUIStyle _rowStyle, _rowSelStyle, _headerStyle, _emptyStyle, _renameStyle;

        static GUIStyle RowStyle => _rowStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
            padding = new RectOffset(0, 2, 0, 0),
        };

        static GUIStyle RowSelStyle => _rowSelStyle ??= new GUIStyle(RowStyle)
        {
            normal = { textColor = Color.white },
        };

        static GUIStyle HeaderStyle => _headerStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        };

        static GUIStyle EmptyStyle => _emptyStyle ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
        };

        static GUIStyle RenameStyle => _renameStyle ??= new GUIStyle(EditorStyles.textField)
        {
            alignment = TextAnchor.MiddleLeft,
        };

        static bool Pro => EditorGUIUtility.isProSkin;
        static Color BgColor => Pro ? new Color(0.20f, 0.20f, 0.20f) : new Color(0.78f, 0.78f, 0.78f);
        static Color HeaderColor => Pro ? new Color(0.27f, 0.27f, 0.27f) : new Color(0.67f, 0.67f, 0.67f);
        static Color DividerColor => Pro ? new Color(0.13f, 0.13f, 0.13f) : new Color(0.50f, 0.50f, 0.50f);
        static Color SelColor => new Color(0.24f, 0.48f, 0.90f, 0.9f);
        static Color HoverColor => new Color(1f, 1f, 1f, 0.07f);
        static Color TrackColor => Pro ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.68f, 0.68f, 0.68f);
        static Color ThumbColor => Pro ? new Color(0.45f, 0.45f, 0.45f) : new Color(0.52f, 0.52f, 0.52f);
        static Color DropColor => new Color(0.30f, 0.80f, 0.45f, 0.35f);
        static Color DropBorderColor => new Color(0.35f, 0.85f, 0.50f, 0.95f);
        static Color PingColor => new Color(1f, 0.85f, 0.30f, 1f); // alpha applied per-frame by the flash

        /// <summary>Draws a 1px outline just inside <paramref name="r"/>.</summary>
        static void DrawBorder(Rect r, Color color)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), color);
        }
    }
}
