using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Spells out an asset name the Project window had to cut short, in a small popup beside
    /// the cursor, while the mouse rests on its row. A name that ends in "..." can otherwise
    /// only be read by selecting the asset, renaming it, or widening the pane — which is
    /// exactly what hunting through a folder full of near-identically named prefabs needs.
    ///
    /// Both of Tabstep's views feed it: the type-column view from its own rows, and the
    /// embedded stock browser through <see cref="EditorApplication.projectWindowItemOnGUI"/>,
    /// which fires for the icon grid, the list and the folder tree alike. Stock Project
    /// windows keep their own behaviour: the hook only feeds while a Tabstep window is
    /// painting its browser (see <see cref="BeginBrowserItems"/>).
    ///
    /// Rows feed it as they are laid out, and <see cref="EndPass"/> — last in the owning
    /// window's OnGUI, so the popup lands on top of every pane it may overlap — decides what
    /// to do with that. Only a Repaint pass can paint or wipe anything, so a MouseMove pass
    /// merely asks for one; the stock list never repaints on its own account when the mouse
    /// moves, and without that nudge the cursor would go unnoticed there.
    /// </summary>
    static class AssetNameOverlay
    {
        // Tooltip manners: a cursor crossing the list on its way somewhere else must not
        // flash a popup, so nothing opens until it has rested on the same row this long.
        const double Delay = 0.35;
        const float CursorOffsetX = 16f;
        const float CursorOffsetY = 18f;
        const float PadX = 6f;
        const float PadY = 3f;
        // Left edge a one-line row (the stock list, the folder tree) spends on its icon,
        // and the margin a grid cell keeps around the name under its thumbnail. Only used
        // to judge whether the name fit — the browser draws the label itself.
        const float RowIconGutter = 22f;
        const float GridLabelMargin = 4f;
        const float OneLineRowHeight = 24f;

        // Set for the duration of one hosted-browser pass: the window whose items the
        // projectWindowItemOnGUI hook may feed. Null while any other Project window paints.
        static EditorWindow _browserPass;

        // The popup itself: what it says, where the cursor was when it opened, and which
        // window owns it — two Tabstep windows must not clear each other's.
        static EditorWindow _owner;
        static string _text;
        static Vector2 _screenAnchor;
        static double _since;
        static bool _fed;
        static bool _suppressed;

        static readonly GUIContent Content = new GUIContent();

        [InitializeOnLoadMethod]
        static void Install()
        {
            // Additive, and paired with a remove so a domain reload cannot stack it twice:
            // other packages hang their own overlays (version control, asset icons) on the
            // same hook. The instance-id variant, not the guid one: a sub-asset row shares
            // its parent's guid, so only the instance id says which row it really is.
            EditorApplication.projectWindowItemInstanceOnGUI -= OnProjectWindowItem;
            EditorApplication.projectWindowItemInstanceOnGUI += OnProjectWindowItem;
        }

        static bool Enabled => TabstepSettings.HoverShowsFullName;

        /// <summary>True once the cursor has rested long enough for the popup to be up.</summary>
        static bool Shown => _text != null && EditorApplication.timeSinceStartup - _since >= Delay;

        /// <summary>
        /// Latches the dismissal rules from the events themselves: a drag, a click or the
        /// cursor leaving the window takes the popup away, and nothing reopens it until the
        /// mouse is idle again. A Repaint in the middle of a drag carries no sign of one, so
        /// it has to be remembered rather than tested for. Called once per pass by the
        /// owning window, before anything feeds.
        /// </summary>
        public static void TrackEvent(EditorWindow owner)
        {
            switch (Event.current.type)
            {
                case EventType.MouseDown:
                case EventType.MouseDrag:
                case EventType.DragUpdated:
                case EventType.DragPerform:
                // Scrolling moves the rows out from under a cursor that never moved, so
                // whatever the popup is naming is about to stop being true.
                case EventType.ScrollWheel:
                    _suppressed = true;
                    if (Clear()) owner.Repaint();
                    break;
                case EventType.MouseUp:
                case EventType.MouseMove:
                case EventType.DragExited:
                    _suppressed = false;
                    break;
                case EventType.MouseLeaveWindow:
                    if (Clear()) owner.Repaint();
                    break;
            }
        }

        /// <summary>
        /// Opens the pass in which the embedded browser's items may feed the popup, on behalf
        /// of <paramref name="owner"/>. Null while its rows are not the ones under the cursor
        /// — they are covered by the type-column view, say.
        /// </summary>
        public static void BeginBrowserItems(EditorWindow owner) => _browserPass = owner;

        /// <summary>Closes it again — must run even when the browser exits the GUI.</summary>
        public static void EndBrowserItems() => _browserPass = null;

        /// <summary>
        /// Offers the row under the cursor. The popup only opens when <paramref name="name"/>
        /// really does not fit <paramref name="available"/> pixels in
        /// <paramref name="style"/>: a name already drawn in full needs no second copy of
        /// itself beside the cursor.
        /// </summary>
        public static void Offer(EditorWindow owner, string name, float available, GUIStyle style)
        {
            if (!Enabled || _suppressed || owner == null || string.IsNullOrEmpty(name)) return;
            if (!IsFeedPass(Event.current.type)) return;
            Content.text = name;
            if ((style ?? EditorStyles.label).CalcSize(Content).x <= available) return;

            if (name != _text || owner != _owner)
            {
                // Hand the popup over: the window that had it repaints to wipe its pixels.
                if (_owner != null && _owner != owner) _owner.Repaint();
                _text = name;
                _owner = owner;
                _since = EditorApplication.timeSinceStartup;
                _screenAnchor = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
            }
            else if (!Shown)
            {
                // Still inside the delay: keep following the cursor, so the popup opens
                // where it came to rest rather than where it first crossed the row.
                _screenAnchor = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
            }
            _fed = true;
        }

        /// <summary>
        /// Draws the popup, or takes it away again — last in <paramref name="owner"/>'s OnGUI,
        /// after every row has had its chance to feed. A row that stopped feeding (the cursor
        /// left it, or its name fits after all) closes the popup here.
        /// </summary>
        public static void EndPass(EditorWindow owner)
        {
            var type = Event.current.type;
            if (_owner != owner) return; // another window owns it
            if (type == EventType.MouseMove)
            {
                // Paint nothing on a MouseMove pass — just note that the picture is now
                // possibly stale, either because a row under the cursor wants a popup or
                // because one is up and may need wiping, and ask for the repaint that sorts
                // it out. The Repaint pass feeds again from scratch.
                bool fed = _fed;
                _fed = false;
                if (fed || _text != null) owner.Repaint();
                return;
            }
            if (type != EventType.Repaint) return;
            if (!_fed)
            {
                Clear();
                return;
            }
            _fed = false;
            if (!Shown)
            {
                owner.Repaint(); // come back once the delay is up and open it then
                return;
            }
            DrawBox(owner);
        }

        /// <summary>Passes a row may feed in: the two that know where the mouse is.</summary>
        static bool IsFeedPass(EventType type) =>
            type == EventType.Repaint || type == EventType.MouseMove;

        /// <summary>Closes the popup. True when one was actually up, i.e. pixels need wiping.</summary>
        public static bool Clear()
        {
            bool showing = Shown;
            _text = null;
            _owner = null;
            _fed = false;
            return showing;
        }

        /// <summary>
        /// The embedded browser's own rows, by way of Unity's per-item hook — the only
        /// account of that layout available to us, and one that covers the icon grid, the
        /// list and the folder tree in one go.
        ///
        /// The hook hands out the whole row, never the label inside it, so how much room the
        /// name had has to be inferred. Erring towards "it fit" keeps the popup from opening
        /// over names that are perfectly readable; the cost is that a deeply indented folder
        /// in the tree can keep its cut-off name to itself, its indent being unknown here.
        /// </summary>
        static void OnProjectWindowItem(int instanceID, Rect itemRect)
        {
            if (_browserPass == null || !Enabled || _suppressed) return;
            var e = Event.current;
            if (e == null || !IsFeedPass(e.type)) return;
            if (!itemRect.Contains(e.mousePosition)) return;
            // The row height tells the layouts apart: a list or tree row is one line tall
            // and spends its left edge on the icon, while a grid cell is a thumbnail with
            // the name written underneath it.
            bool oneLine = itemRect.height < OneLineRowHeight;
            float available = itemRect.width - (oneLine ? RowIconGutter : GridLabelMargin);
            Offer(_browserPass, ItemName(instanceID), available, EditorStyles.label);
        }

        /// <summary>
        /// The name the browser wrote in that row: a sub-asset's own (its path is its
        /// parent's file, which would name the wrong thing), otherwise the file or folder
        /// name as the Project window shows it — no extension, folders whole.
        /// </summary>
        static string ItemName(int instanceID)
        {
            if (AssetDatabase.IsSubAsset(instanceID))
            {
                var sub = EditorUtility.InstanceIDToObject(instanceID);
                return sub != null ? sub.name : null;
            }
            var path = AssetDatabase.GetAssetPath(instanceID);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.IsValidFolder(path)
                ? Path.GetFileName(path)
                : Path.GetFileNameWithoutExtension(path);
        }

        static void DrawBox(EditorWindow owner)
        {
            Content.text = _text;
            var style = BoxStyle;
            var size = style.CalcSize(Content);
            var anchor = GUIUtility.ScreenToGUIPoint(_screenAnchor);
            var rect = new Rect(anchor.x + CursorOffsetX, anchor.y + CursorOffsetY,
                size.x + PadX * 2, size.y + PadY * 2);

            // Keep the box inside the window: slide it left, and flip it above the cursor
            // when there is no room below.
            float maxX = owner.position.width;
            float maxY = owner.position.height;
            rect.width = Mathf.Min(rect.width, maxX);
            if (rect.xMax > maxX) rect.x = maxX - rect.width;
            if (rect.yMax > maxY) rect.y = anchor.y - rect.height - 4f;
            rect.x = Mathf.Max(0f, rect.x);
            rect.y = Mathf.Max(0f, rect.y);

            EditorGUI.DrawRect(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), ShadowColor);
            EditorGUI.DrawRect(rect, BackColor);
            DrawBorder(rect, BorderColor);
            GUI.Label(new Rect(rect.x + PadX, rect.y + PadY, rect.width - PadX * 2, rect.height - PadY * 2),
                Content, style);
        }

        static void DrawBorder(Rect r, Color color)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), color);
        }

        static GUIStyle _boxStyle;

        static GUIStyle BoxStyle => _boxStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
            padding = new RectOffset(0, 0, 0, 0),
        };

        static bool Pro => EditorGUIUtility.isProSkin;
        static Color BackColor => Pro
            ? new Color(0.13f, 0.13f, 0.13f, 0.97f)
            : new Color(0.96f, 0.96f, 0.96f, 0.98f);
        static Color BorderColor => Pro
            ? new Color(0.40f, 0.40f, 0.40f)
            : new Color(0.52f, 0.52f, 0.52f);
        static Color ShadowColor => new Color(0f, 0f, 0f, 0.22f);
    }
}
