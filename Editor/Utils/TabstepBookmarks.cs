using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// The Quick Access folder list (right-click on the + tab button). Stored per
    /// user and per project in UserSettings/ — not in EditorPrefs — because the
    /// paths only mean something inside this project.
    /// </summary>
    static class TabstepBookmarks
    {
        [Serializable]
        class Payload
        {
            public List<string> folders = new List<string>();
        }

        static Payload _payload; // cache; dropped on domain reload, reloaded lazily

        static string FilePath
        {
            get
            {
                var projectRoot = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
                return Path.Combine(projectRoot, "UserSettings", "TabstepQuickAccess.json");
            }
        }

        public static IReadOnlyList<string> Folders => Load().folders;

        public static bool Contains(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            return folderPath != null && Load().folders.Contains(folderPath);
        }

        public static void Add(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath == null || Load().folders.Contains(folderPath)) return;
            Load().folders.Add(folderPath);
            Save();
        }

        public static void Remove(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath != null && Load().folders.Remove(folderPath))
                Save();
        }

        static Payload Load()
        {
            if (_payload != null) return _payload;
            _payload = new Payload();
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonUtility.FromJson<Payload>(File.ReadAllText(FilePath));
                    if (loaded?.folders != null) _payload = loaded;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not read the Quick Access list: {e.Message}");
            }
            return _payload;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(Load(), true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not save the Quick Access list: {e.Message}");
            }
        }
    }
}
