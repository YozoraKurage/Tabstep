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

        /// <summary>Show the back/forward/up + breadcrumb bar under the tabs.</summary>
        public static bool ShowNavigationBar
        {
            get => EditorPrefs.GetBool(Prefix + "ShowNavigationBar", true);
            set => EditorPrefs.SetBool(Prefix + "ShowNavigationBar", value);
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

        public static void ResetAll()
        {
            string[] keys =
            {
                "NewTabFolder", "MiddleClickClosesTab", "ShowNavigationBar", "MaxTabTitleLength",
                "PingOpensNewTab",
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
                    "inspector", "ping", "double click",
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
            TabstepSettings.MaxTabTitleLength = EditorGUILayout.IntSlider(
                new GUIContent("Max Tab Title Length"),
                TabstepSettings.MaxTabTitleLength, 4, 60);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.ShowNavigationBar = EditorGUILayout.Toggle(
                new GUIContent("Navigation Bar",
                    "Back / forward / up buttons and the clickable breadcrumb path."),
                TabstepSettings.ShowNavigationBar);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Behavior", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            TabstepSettings.PingOpensNewTab = EditorGUILayout.Toggle(
                new GUIContent("Ping Opens New Tab",
                    "When something outside the window (an Inspector object field, \"Show in Project\"...) " +
                    "changes the shown folder, open it as a new tab instead of replacing the current one."),
                TabstepSettings.PingOpensNewTab);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Reset To Defaults", GUILayout.Width(160)))
                TabstepSettings.ResetAll();
        }
    }
}
