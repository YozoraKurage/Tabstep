using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Yozolab.Tabstep
{
    partial class TabstepProjectWindow
    {
        // ---- multi-window --------------------------------------------------------

        /// <summary>
        /// Creates an additional floating Tabstep window showing the given tabs.
        /// Unlike <see cref="Open"/> (which focuses the existing window) this always
        /// makes a new one — any number of Tabstep windows can coexist; each owns its
        /// own session and embedded browser.
        /// </summary>
        static TabstepProjectWindow CreateNewWindow(TabSession session, Rect screenRect)
        {
            var window = CreateInstance<TabstepProjectWindow>();
            // OnEnable already ran (with an empty session, opening a default tab) —
            // the prepared session simply replaces it before the first OnGUI.
            window._session = session;
            window._applyTabToBrowser = true;
            window.minSize = new Vector2(400, 250);
            // Show(): a regular floating editor window — dockable, saved into the layout.
            window.Show();
            window.position = screenRect;
            window.Focus();
            return window;
        }

        /// <summary>Opens a copy of the tab (history included) in a new window.</summary>
        void OpenTabInNewWindow(int index)
        {
            if (index < 0 || index >= _session.Count) return;
            var session = new TabSession();
            session.Add(_session.Tabs[index].Clone());
            CreateNewWindow(session, CascadePosition());
        }

        /// <summary>
        /// Moves the tab (history included) out into its own window, docked into the
        /// layout right beside this one — as if its tab had been dropped on this
        /// pane's right edge. When docking is unavailable the new window stays
        /// floating at this window's right side instead.
        /// </summary>
        void SeparateTab(int index)
        {
            if (_session.Count <= 1) return; // moving the only tab would just relocate the window
            var tab = _session.DetachTab(index);
            if (tab == null) return;
            tab.Pinned = false; // the pin belongs to the old window's slot
            var session = new TabSession();
            session.Add(tab);
            var window = CreateNewWindow(session, RightSidePosition());
            WindowDocking.DockRightOf(this, window);
            _applyTabToBrowser = true;
            Repaint();
        }

        /// <summary>Clones this whole window — every tab, pins and the active tab included.</summary>
        void DuplicateWindow()
        {
            CreateNewWindow(_session.Clone(), CascadePosition());
        }

        Rect CascadePosition()
        {
            return ClampToMainWindow(new Rect(
                position.x + 28f, position.y + 28f, position.width, position.height));
        }

        Rect RightSidePosition()
        {
            return ClampToMainWindow(new Rect(
                position.xMax + 6f, position.y, position.width, position.height));
        }

        /// <summary>Keeps enough of a new window over the main window to stay grabbable.</summary>
        static Rect ClampToMainWindow(Rect rect)
        {
            var main = EditorGUIUtility.GetMainWindowPosition();
            rect.x = Mathf.Clamp(rect.x, main.x - rect.width + 80f, Mathf.Max(main.x, main.xMax - 80f));
            rect.y = Mathf.Clamp(rect.y, main.y, Mathf.Max(main.y, main.yMax - 80f));
            return rect;
        }
    }
}
