using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>User preferences for Tabstep, backed by EditorPrefs.</summary>
    static class TabstepSettings
    {
        const string Prefix = "Yozolab.Tabstep.";

        /// <summary>Folder a fresh tab opens at (Ctrl+T / the + button).</summary>
        public static string NewTabFolder
        {
            get => EditorPrefs.GetString(Prefix + "NewTabFolder", ProjectPaths.AssetsRoot);
            set => EditorPrefs.SetString(Prefix + "NewTabFolder", value);
        }

        public static bool MiddleClickClosesTab
        {
            get => EditorPrefs.GetBool(Prefix + "MiddleClickClosesTab", true);
            set => EditorPrefs.SetBool(Prefix + "MiddleClickClosesTab", value);
        }

        /// <summary>New tabs open right of the active tab instead of at the end of the bar.</summary>
        public static bool NewTabBesideActive
        {
            get => EditorPrefs.GetBool(Prefix + "NewTabBesideActive", false);
            set => EditorPrefs.SetBool(Prefix + "NewTabBesideActive", value);
        }

        /// <summary>
        /// Give every unpinned tab the same width, shrinking them to share the bar like a
        /// web browser (down to a minimum, after which the bar scrolls). Off sizes each tab
        /// to its title.
        /// </summary>
        public static bool EqualWidthTabs
        {
            get => EditorPrefs.GetBool(Prefix + "EqualWidthTabs", true);
            set => EditorPrefs.SetBool(Prefix + "EqualWidthTabs", value);
        }

        /// <summary>Status bar at the bottom: folder item count and selection summary.</summary>
        public static bool ShowStatusBar
        {
            get => EditorPrefs.GetBool(Prefix + "ShowStatusBar", true);
            set => EditorPrefs.SetBool(Prefix + "ShowStatusBar", value);
        }

        /// <summary>Dragging an item out of the shelf consumes it (it is a hand-off, not a copy source).</summary>
        public static bool ShelfOneShot
        {
            get => EditorPrefs.GetBool(Prefix + "ShelfOneShot", true);
            set => EditorPrefs.SetBool(Prefix + "ShelfOneShot", value);
        }

        /// <summary>
        /// Show the navigation bar (back/forward/up + address bar) under the tabs. It
        /// replaces the browser's own "Assets &gt; ..." path header, and — with Harmony
        /// present — absorbs the browser's toolbar (create button + search field) too.
        /// Off restores the stock browser chrome.
        /// </summary>
        public static bool ShowNavigationBar
        {
            get => EditorPrefs.GetBool(Prefix + "ShowNavigationBar", true);
            set => EditorPrefs.SetBool(Prefix + "ShowNavigationBar", value);
        }

        /// <summary>Mouse back/forward (side) buttons navigate the active tab's history.</summary>
        public static bool MouseSideButtonsNavigate
        {
            get => EditorPrefs.GetBool(Prefix + "MouseSideButtonsNavigate", true);
            set => EditorPrefs.SetBool(Prefix + "MouseSideButtonsNavigate", value);
        }

        /// <summary>Tab title length before it gets ellipsized.</summary>
        public static int MaxTabTitleLength
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(Prefix + "MaxTabTitleLength", 20), 4, 60);
            set => EditorPrefs.SetInt(Prefix + "MaxTabTitleLength", Mathf.Clamp(value, 4, 60));
        }

        /// <summary>Folder changes pushed from outside (pings, "Show in Project") open a new tab.</summary>
        public static bool PingOpensNewTab
        {
            get => EditorPrefs.GetBool(Prefix + "PingOpensNewTab", true);
            set => EditorPrefs.SetBool(Prefix + "PingOpensNewTab", value);
        }

        /// <summary>
        /// Allow dropping assets onto folder entries in the type-column view to move them
        /// there. Off by default — the view normally swallows drags so they fall through to
        /// real targets (scene, object fields, the folder tree).
        /// </summary>
        public static bool ColumnViewFolderDrop
        {
            get => EditorPrefs.GetBool(Prefix + "ColumnViewFolderDrop", false);
            set => EditorPrefs.SetBool(Prefix + "ColumnViewFolderDrop", value);
        }

        public static void ResetAll()
        {
            string[] keys =
            {
                "NewTabFolder", "MiddleClickClosesTab", "ShowNavigationBar", "MaxTabTitleLength",
                "PingOpensNewTab", "MouseSideButtonsNavigate", "NewTabBesideActive", "ShelfOneShot",
                "ShowStatusBar", "ColumnViewFolderDrop", "EqualWidthTabs",
            };
            foreach (var key in keys)
                EditorPrefs.DeleteKey(Prefix + key);
        }
    }

    /// <summary>Exposes <see cref="TabstepSettings"/> in Edit &gt; Preferences.</summary>
    static class TabstepSettingsProvider
    {
        public const string Path = "Preferences/Yozolab/Tabstep";

        [SettingsProvider]
        static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.User)
            {
                label = "Tabstep",
                guiHandler = _ => DrawGui(),
                keywords = new HashSet<string>(new[]
                {
                    "project", "browser", "tab", "explorer", "breadcrumb", "history",
                    "inspector", "ping", "double click", "mouse", "side button", "path header",
                    "shelf", "pin", "quick access", "bookmark", "reorder", "selection",
                }),
            };
        }

        static void DrawGui()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Tabs", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.NewTabFolder = EditorGUILayout.TextField(
                new GUIContent("New Tab Folder",
                    "Folder a new tab starts at. Falls back to Assets when the path does not exist."),
                TabstepSettings.NewTabFolder);
            TabstepSettings.MiddleClickClosesTab = EditorGUILayout.Toggle(
                new GUIContent("Middle-Click Closes Tab"),
                TabstepSettings.MiddleClickClosesTab);
            TabstepSettings.NewTabBesideActive = EditorGUILayout.Toggle(
                new GUIContent("Open New Tab Beside Active",
                    "New tabs open right of the active tab instead of at the end of the bar."),
                TabstepSettings.NewTabBesideActive);
            TabstepSettings.EqualWidthTabs = EditorGUILayout.Toggle(
                new GUIContent("Equal-Width Tabs",
                    "Give every unpinned tab the same width, shrinking them to share the bar " +
                    "like a web browser. Off sizes each tab to its title."),
                TabstepSettings.EqualWidthTabs);
            TabstepSettings.MaxTabTitleLength = EditorGUILayout.IntSlider(
                new GUIContent("Max Tab Title Length"),
                TabstepSettings.MaxTabTitleLength, 4, 60);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.ShowNavigationBar = EditorGUILayout.Toggle(
                new GUIContent("Navigation Bar",
                    "Back / forward / up buttons and the clickable breadcrumb path. Replaces " +
                    "the browser's own \"Assets > ...\" path header and, with Harmony present, " +
                    "absorbs its toolbar (create + search) too. When off, the stock browser " +
                    "chrome is shown instead."),
                TabstepSettings.ShowNavigationBar);
            TabstepSettings.ShowStatusBar = EditorGUILayout.Toggle(
                new GUIContent("Status Bar",
                    "A bottom row showing the current folder's item count and a summary of " +
                    "the selected assets (count and file size)."),
                TabstepSettings.ShowStatusBar);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Behavior", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.MouseSideButtonsNavigate = EditorGUILayout.Toggle(
                new GUIContent("Mouse Side Buttons Navigate",
                    "The mouse back/forward (thumb) buttons move through the active tab's " +
                    "history, like a web browser."),
                TabstepSettings.MouseSideButtonsNavigate);
            TabstepSettings.PingOpensNewTab = EditorGUILayout.Toggle(
                new GUIContent("Ping Opens New Tab",
                    "When something outside the window (an Inspector object field, \"Show in Project\"...) " +
                    "changes the shown folder, open it as a new tab instead of replacing the current one."),
                TabstepSettings.PingOpensNewTab);
            TabstepSettings.ColumnViewFolderDrop = EditorGUILayout.Toggle(
                new GUIContent("Column View Folder Drop",
                    "In the type-column view, allow dropping assets onto a folder entry to move " +
                    "them into it. Off by default — drags over the view are otherwise ignored so " +
                    "they pass through to the scene, object fields and the folder tree."),
                TabstepSettings.ColumnViewFolderDrop);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Shelf", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.ShelfOneShot = EditorGUILayout.Toggle(
                new GUIContent("One-Shot Items",
                    "Dragging an item out of the shelf consumes it — the shelf is a hand-off, " +
                    "not a copy source. Off keeps items until removed manually. " +
                    "Locked items are never consumed."),
                TabstepSettings.ShelfOneShot);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Reset To Defaults", GUILayout.Width(160)))
                TabstepSettings.ResetAll();
        }
    }
}
