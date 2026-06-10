using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Named tab sets ("UI work", "character work"...) that can be saved and loaded
    /// from the tab-list dropdown. A workspace is a deep copy of a whole
    /// <see cref="TabSession"/> — tabs, order, pins, histories and searches included.
    /// Stored per user and per project in UserSettings/.
    /// </summary>
    static class TabstepWorkspaces
    {
        [Serializable]
        class Workspace
        {
            public string name;
            public TabSession session;
        }

        [Serializable]
        class Payload
        {
            public List<Workspace> workspaces = new List<Workspace>();
        }

        static Payload _payload; // cache; dropped on domain reload, reloaded lazily

        static string FilePath
        {
            get
            {
                var projectRoot = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
                return Path.Combine(projectRoot, "UserSettings", "TabstepWorkspaces.json");
            }
        }

        public static IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>();
                foreach (var workspace in Load().workspaces)
                    names.Add(workspace.name);
                return names;
            }
        }

        /// <summary>Saves a deep copy of the session, replacing a workspace of the same name.</summary>
        public static void Save(string name, TabSession session)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || session == null) return;
            var payload = Load();
            payload.workspaces.RemoveAll(w => w.name == name);
            payload.workspaces.Add(new Workspace { name = name, session = DeepCopy(session) });
            SaveFile();
        }

        /// <summary>A fresh copy of the stored session, or null when the name is unknown.</summary>
        public static TabSession Get(string name)
        {
            var workspace = Load().workspaces.Find(w => w.name == name);
            return workspace?.session == null ? null : DeepCopy(workspace.session);
        }

        public static void Delete(string name)
        {
            if (Load().workspaces.RemoveAll(w => w.name == name) > 0)
                SaveFile();
        }

        /// <summary>Serialization round-trip — keeps stored sessions detached from live ones.</summary>
        static TabSession DeepCopy(TabSession session)
        {
            return JsonUtility.FromJson<TabSession>(JsonUtility.ToJson(session));
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
                    if (loaded?.workspaces != null) _payload = loaded;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not read the workspace list: {e.Message}");
            }
            return _payload;
        }

        static void SaveFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(Load(), true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tabstep] Could not save the workspace list: {e.Message}");
            }
        }
    }
}
