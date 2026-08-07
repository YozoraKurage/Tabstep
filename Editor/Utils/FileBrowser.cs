using System.IO;
using UnityEditor;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Opening project folders in the OS file browser.
    ///
    /// EditorUtility.RevealInFinder alone is not enough: handed a directory it opens the
    /// PARENT and selects the directory there on Windows and macOS (Linux is the only
    /// platform that opens the directory itself) — "selected", not "opened".
    /// EditorUtility.OpenWithDefaultApp hands the path to the OS's default handler, and a
    /// directory's default handler is the file browser, which opens it.
    /// </summary>
    static class FileBrowser
    {
        /// <summary>
        /// Menu wording for opening a folder, following the platform the way Unity's own
        /// entries do. A constant per platform rather than a runtime check because
        /// <see cref="MenuItem"/> paths must be compile-time constants.
        /// </summary>
#if UNITY_EDITOR_OSX
        public const string OpenFolderLabel = "Open Folder in Finder";
#elif UNITY_EDITOR_WIN
        public const string OpenFolderLabel = "Open Folder in Explorer";
#else
        public const string OpenFolderLabel = "Open Folder in File Browser";
#endif

        /// <summary>Absolute path of a project path, or the input when it cannot be resolved.</summary>
        public static string ToAbsolutePath(string projectPath)
        {
            try
            {
                // GetPhysicalPath resolves Packages/... into the real package location.
                return Path.GetFullPath(FileUtil.GetPhysicalPath(projectPath));
            }
            catch
            {
                return projectPath;
            }
        }

        /// <summary>True while the folder still exists on disk (Packages/... included).</summary>
        public static bool FolderExists(string projectPath)
        {
            var folder = ProjectPaths.Normalize(projectPath);
            return folder != null && Directory.Exists(ToAbsolutePath(folder));
        }

        /// <summary>
        /// Opens the folder in the OS file browser, with the folder's own contents shown.
        /// Returns false when the folder is gone, so callers can report that.
        /// </summary>
        public static bool OpenFolder(string projectPath)
        {
            if (!FolderExists(projectPath)) return false;
            EditorUtility.OpenWithDefaultApp(ToAbsolutePath(ProjectPaths.Normalize(projectPath)));
            return true;
        }
    }
}
