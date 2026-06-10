using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Hosts an instance of Unity's internal ProjectBrowser (the stock Project window)
    /// inside another EditorWindow. ProjectBrowser is internal, so instead of inheriting
    /// it directly this wrapper instantiates it and delegates OnGUI via reflection —
    /// the embedded pane is the real Project window, including its toolbar and search.
    ///
    /// Everything reflective is null-guarded: if a future Unity version renames a member,
    /// <see cref="IsAvailable"/> turns false and the window shows a fallback message
    /// instead of throwing.
    /// </summary>
    class ProjectBrowserHost : IDisposable
    {
        static readonly Type BrowserType =
            typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ProjectBrowser");

        static readonly FieldInfo ParentField =
            typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo PosField =
            typeof(EditorWindow).GetField("m_Pos", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly MethodInfo OnGUIMethod = FindMethod("OnGUI", 0);
        static readonly MethodInfo InitIfNeededMethod = FindMethod("InitIfNeeded", 0);
        static readonly MethodInfo GetActiveFolderPathMethod = FindMethod("GetActiveFolderPath", 0);
        static readonly MethodInfo SetTwoColumnsMethod = FindMethod("SetTwoColumns", 0);

        // internal void SetFolderSelection(int[] selectedInstanceIDs, bool revealSelectionAndFrameLastSelected)
        static readonly MethodInfo SetFolderSelectionMethod = BrowserType?
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m =>
            {
                if (m.Name != "SetFolderSelection") return false;
                var p = m.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(int[]) && p[1].ParameterType == typeof(bool);
            });

        static MethodInfo FindMethod(string name, int paramCount)
        {
            return BrowserType?
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == paramCount);
        }

        /// <summary>False when the internal API this relies on is missing in the running Unity version.</summary>
        public static bool IsAvailable =>
            BrowserType != null && PosField != null && ParentField != null &&
            OnGUIMethod != null && GetActiveFolderPathMethod != null && SetFolderSelectionMethod != null;

        readonly EditorWindow _owner;
        readonly HashSet<string> _warnedMethods = new HashSet<string>();
        EditorWindow _browser;

        public ProjectBrowserHost(EditorWindow owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_browser != null)
            {
                // Detach the borrowed host view before destruction so teardown code
                // never mistakes the browser for a real docked window.
                ParentField?.SetValue(_browser, null);
                Object.DestroyImmediate(_browser);
            }
            _browser = null;
        }

        bool EnsureBrowser()
        {
            if (_browser != null) return true;
            if (!IsAvailable) return false;
            _browser = ScriptableObject.CreateInstance(BrowserType) as EditorWindow;
            if (_browser == null) return false;
            // Never saved into the layout file — the host window owns and recreates it.
            _browser.hideFlags = HideFlags.HideAndDontSave;
            AttachToOwner();
            Invoke(InitIfNeededMethod);
            // Tabs target folders, which only the two-column mode can display directly.
            Invoke(SetTwoColumnsMethod);
            return true;
        }

        /// <summary>
        /// The browser repaints and resolves focus through m_Parent (its HostView). It is
        /// never shown as a real window, so it borrows the host's. Re-attached every frame
        /// because docking/undocking swaps the owner's parent.
        /// </summary>
        void AttachToOwner()
        {
            var parent = ParentField.GetValue(_owner);
            if (parent != null && !ReferenceEquals(ParentField.GetValue(_browser), parent))
                ParentField.SetValue(_browser, parent);
        }

        object Invoke(MethodInfo method, params object[] args)
        {
            if (method == null || _browser == null) return null;
            try
            {
                return method.Invoke(_browser, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is ExitGUIException)
            {
                // GUIUtility.ExitGUI() inside the browser (drag start, object selector...)
                // must keep flowing as ExitGUIException for IMGUI to unwind normally.
                throw e.InnerException;
            }
            catch (Exception e)
            {
                // Warn once per member — OnGUI runs every frame and must not spam the console.
                if (_warnedMethods.Add(method.Name))
                    Debug.LogWarning(
                        $"[Tabstep] ProjectBrowser call '{method.Name}' failed: {e.InnerException ?? e}");
                return null;
            }
        }

        /// <summary>Draws the embedded Project browser into <paramref name="rect"/> (window coordinates).</summary>
        public void OnGUI(Rect rect)
        {
            if (!EnsureBrowser()) return;
            AttachToOwner();
            // ProjectBrowser lays itself out against `position`; only the size matters here
            // because rendering happens inside a GUI area at the rect's origin.
            PosField.SetValue(_browser, new Rect(0, 0, rect.width, rect.height));
            GUILayout.BeginArea(rect);
            try
            {
                Invoke(OnGUIMethod);
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        /// <summary>Folder currently shown by the embedded browser, or null when unknown.</summary>
        public string GetActiveFolderPath()
        {
            if (_browser == null) return null;
            return ProjectPaths.Normalize(Invoke(GetActiveFolderPathMethod) as string);
        }

        /// <summary>Points the embedded browser at a folder. False when the folder no longer exists.</summary>
        public bool ShowFolder(string projectPath)
        {
            if (!EnsureBrowser()) return false;
            if (string.IsNullOrEmpty(projectPath) || !AssetDatabase.IsValidFolder(projectPath)) return false;
            var folder = AssetDatabase.LoadAssetAtPath<Object>(projectPath);
            if (folder == null) return false;
            Invoke(InitIfNeededMethod);
            Invoke(SetFolderSelectionMethod, new[] { folder.GetInstanceID() }, false);
            return true;
        }
    }
}
