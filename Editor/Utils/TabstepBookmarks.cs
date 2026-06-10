using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>A bookmarked search: a filter pinned to the folder it was made in.</summary>
    [Serializable]
    class SavedSearch
    {
        public string folder;
        public string search;
    }

    /// <summary>
    /// The Quick Access lists (right-click on the + tab button): folders, plus saved
    /// searches that reopen as a tab with their filter applied. Stored per user and
    /// per project in UserSettings/ — not in EditorPrefs — because the paths only
    /// mean something inside this project.
    /// </summary>
    static class TabstepBookmarks
    {
        [Serializable]
        class Payload
        {
            public List<string> folders = new List<string>();
            public List<SavedSearch> searches = new List<SavedSearch>();
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

        public static IReadOnlyList<SavedSearch> Searches => Load().searches;

        public static bool ContainsSearch(string folderPath, string search)
        {
            return FindSearch(folderPath, search) != null;
        }

        public static void AddSearch(string folderPath, string search)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath == null || string.IsNullOrWhiteSpace(search)) return;
            if (FindSearch(folderPath, search) != null) return;
            Load().searches.Add(new SavedSearch { folder = folderPath, search = search.Trim() });
            Save();
        }

        public static void RemoveSearch(SavedSearch entry)
        {
            if (Load().searches.Remove(entry))
                Save();
        }

        static SavedSearch FindSearch(string folderPath, string search)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            search = search?.Trim();
            return Load().searches.Find(s => s.folder == folderPath && s.search == search);
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
