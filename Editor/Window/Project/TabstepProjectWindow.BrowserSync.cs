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
        /// <summary>
        /// Middle-click on a folder in the embedded asset list opens it in a new tab.
        /// The browser has no middle-click behaviour of its own, so the press is
        /// converted into a left click — which makes the browser select whatever is
        /// under the cursor — and the release reads that selection back. Restricted to
        /// the list area: tree clicks navigate by themselves and must stay untouched.
        /// Returns true on the (converted) release, after which the caller — once the
        /// browser has processed the click — opens the selected folder.
        /// </summary>
        bool ConvertBrowserMiddleClick(Rect content)
        {
            var e = Event.current;
            if (e.button != 2 || (e.type != EventType.MouseDown && e.type != EventType.MouseUp))
                return false;
            var list = _host.GetListAreaRect();
            if (list.width <= 0f) return false;
            var listRect = new Rect(content.x + list.x, content.y + list.y, list.width, list.height);
            if (e.type == EventType.MouseDown)
            {
                // Only a press that starts in the list arms the release.
                _browserMiddleClickArmed = listRect.Contains(e.mousePosition);
                if (_browserMiddleClickArmed) e.button = 0;
                return false;
            }
            if (!_browserMiddleClickArmed) return false;
            _browserMiddleClickArmed = false;
            if (!listRect.Contains(e.mousePosition)) return false;
            e.button = 0;
            return true;
        }

        void OpenSelectedFolderInNewTab()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                OpenInNewTab(path);
        }

        /// <summary>Navigates the active tab into a folder (double-click in the type-column view).</summary>
        void NavigateActiveTab(string folderPath)
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;
            tab.Navigate(folderPath);
            _applyTabToBrowser = true;
            Repaint();
        }

        // ---- browser <-> tab sync --------------------------------------------

        void SyncWithBrowser()
        {
            var tab = _session.ActiveTab;
            if (tab == null) return;

            if (_applyTabToBrowser)
            {
                _applyTabToBrowser = false;
                if (!_host.ShowFolder(tab.CurrentPath))
                {
                    // Folder was deleted/renamed while the tab pointed at it.
                    tab.Reset(ValidFolderOrDefault(null));
                    _host.ShowFolder(tab.CurrentPath);
                }
                _observedBrowserPath = tab.CurrentPath;
                // The browser is shared between tabs, so each tab carries its own
                // search filter and gets it back when it becomes active again.
                var saved = tab.SearchText ?? "";
                if ((_host.GetSearchText() ?? "") != saved)
                    _host.SetSearch(saved);
                _searchText = saved;
                _lastAppliedSearch = _host.GetSearchText() ?? saved;
                return;
            }

            // Mirror the browser's live filter into the active tab (typed into either
            // search field, or cleared by the browser when a folder is clicked).
            var browserSearch = _host.GetSearchText();
            if (browserSearch != null)
                tab.SearchText = browserSearch;

            // The user navigated inside the embedded browser (double-clicked a folder,
            // used the breadcrumb of the browser itself...) — record it in the tab.
            var browserPath = _host.GetActiveFolderPath();
            if (browserPath == null || browserPath == _observedBrowserPath) return;
            _observedBrowserPath = browserPath;
            if (browserPath == tab.CurrentPath) return;

            // The folder changed while another window had focus — the embedded browser
            // was driven from outside (ping from an Inspector object field, "Show in
            // Project"...). Open the destination as a new tab so the current tab keeps
            // its place, like Explorer; in-window navigation stays in the same tab.
            if (TabstepSettings.PingOpensNewTab && focusedWindow != this)
            {
                // If this window already has a tab on the pinged folder, switch to it
                // instead of opening a duplicate.
                int existing = _session.IndexOfFolder(browserPath);
                if (existing >= 0)
                    ActivateTab(existing);
                else
                    _session.OpenTab(browserPath);
            }
            else
                tab.Navigate(browserPath);
        }
    }
}
