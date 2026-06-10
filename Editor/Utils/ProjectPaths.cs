using System;
using System.Collections.Generic;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Pure string helpers for project-relative asset paths ("Assets/...", "Packages/...").
    /// No AssetDatabase calls so the logic stays unit-testable; callers validate existence.
    /// </summary>
    static class ProjectPaths
    {
        public const string AssetsRoot = "Assets";

        /// <summary>Forward slashes, no trailing slash. Returns null for null/empty input.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Replace('\\', '/').TrimEnd('/');
            return path.Length == 0 ? null : path;
        }

        /// <summary>Parent folder path, or null when the path is a root ("Assets", "Packages").</summary>
        public static string GetParent(string path)
        {
            path = Normalize(path);
            if (path == null) return null;
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? null : path.Substring(0, slash);
        }

        /// <summary>Last path segment, used as the tab title.</summary>
        public static string GetDisplayName(string path)
        {
            path = Normalize(path);
            if (path == null) return null;
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        /// <summary>
        /// Each segment paired with its cumulative path, root first:
        /// "Assets/A/B" → (Assets, Assets), (A, Assets/A), (B, Assets/A/B).
        /// </summary>
        public static List<(string name, string path)> GetBreadcrumbs(string path)
        {
            var crumbs = new List<(string, string)>();
            path = Normalize(path);
            if (path == null) return crumbs;
            int start = 0;
            while (start < path.Length)
            {
                int slash = path.IndexOf('/', start);
                int end = slash < 0 ? path.Length : slash;
                crumbs.Add((path.Substring(start, end - start), path.Substring(0, end)));
                start = end + 1;
            }
            return crumbs;
        }

        /// <summary>
        /// Interprets text pasted/typed into the path bar as a project-relative path.
        /// Accepts "Assets/..." and "Packages/..." directly, plus absolute paths under
        /// <paramref name="projectRoot"/> (the folder containing Assets). Surrounding
        /// quotes — as produced by Explorer's "Copy as path" — and stray whitespace are
        /// stripped. Returns null when the text points outside the project.
        /// </summary>
        public static string ToProjectPath(string input, string projectRoot)
        {
            if (string.IsNullOrEmpty(input)) return null;
            input = input.Trim();
            if (input.Length >= 2 && input[0] == '"' && input[input.Length - 1] == '"')
                input = input.Substring(1, input.Length - 2).Trim();
            input = Normalize(input);
            if (input == null) return null;
            if (IsProjectRelative(input)) return input;

            projectRoot = Normalize(projectRoot);
            if (projectRoot != null &&
                input.Length > projectRoot.Length + 1 &&
                input[projectRoot.Length] == '/' &&
                input.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = input.Substring(projectRoot.Length + 1);
                if (IsProjectRelative(relative)) return relative;
            }
            return null;
        }

        static bool IsProjectRelative(string path)
        {
            return IsRootedAt(path, AssetsRoot) || IsRootedAt(path, "Packages");
        }

        static bool IsRootedAt(string path, string root)
        {
            return path.StartsWith(root, StringComparison.Ordinal) &&
                   (path.Length == root.Length || path[root.Length] == '/');
        }

        /// <summary>Shortens a name for tab display, appending an ellipsis when truncated.</summary>
        public static string Ellipsize(string name, int maxChars)
        {
            if (string.IsNullOrEmpty(name) || maxChars <= 1 || name.Length <= maxChars) return name;
            return name.Substring(0, maxChars - 1) + "…";
        }
    }
}
