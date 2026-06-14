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
        // ---- path autocomplete ---------------------------------------------------

        void ClearPathSuggestions()
        {
            _pathSuggestions.Clear();
            _pathSuggestionIndex = -1;
            _pathSuggestionQuery = null;
        }

        /// <summary>Subfolders under the typed prefix; the segment after the last '/' filters them.</summary>
        void UpdatePathSuggestions()
        {
            _pathSuggestionQuery = _pathEditText;
            _pathSuggestions.Clear();
            _pathSuggestionIndex = -1;
            var text = (_pathEditText ?? "").Trim().Trim('"').Replace('\\', '/');
            int slash = text.LastIndexOf('/');
            if (slash < 0)
            {
                foreach (var root in new[] { ProjectPaths.AssetsRoot, "Packages" })
                    if (root.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                        !root.Equals(text, StringComparison.OrdinalIgnoreCase))
                        _pathSuggestions.Add(root);
                return;
            }
            var parent = text.Substring(0, slash);
            var partial = text.Substring(slash + 1);
            // "Packages" is not a folder asset itself but GetSubFolders still lists packages.
            if (parent != "Packages" && !AssetDatabase.IsValidFolder(parent)) return;
            foreach (var sub in AssetDatabase.GetSubFolders(parent))
            {
                var path = ProjectPaths.Normalize(sub);
                var name = ProjectPaths.GetDisplayName(path);
                if (name == null) continue;
                if (partial.Length > 0 && !name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
                _pathSuggestions.Add(path);
                if (_pathSuggestions.Count >= MaxPathSuggestions) break;
            }
        }

        /// <summary>Tab completion: the suggestion becomes the text, ready for the next segment.</summary>
        void AcceptSuggestionIntoText(string path)
        {
            _pathEditText = path + "/";
            MovePathCursorToEnd();
            UpdatePathSuggestions();
            Repaint();
        }

        /// <summary>Clicking or Enter on a suggestion navigates there and ends the edit.</summary>
        void CommitSuggestion(string path)
        {
            _editingPath = false;
            GUIUtility.keyboardControl = 0;
            ClearPathSuggestions();
            NavigateTo(path);
        }

        /// <summary>Completing must not leave the old text selected — put the caret at the end.</summary>
        void MovePathCursorToEnd()
        {
            if (GUIUtility.keyboardControl == 0) return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = _pathEditText;
            editor.cursorIndex = editor.selectIndex = _pathEditText.Length;
        }

        Rect PathSuggestionBoxRect()
        {
            return new Rect(_pathFieldRect.x, _pathFieldRect.yMax + 1, _pathFieldRect.width,
                _pathSuggestions.Count * SuggestionRowHeight + 2);
        }

        Rect PathSuggestionRowRect(int index)
        {
            var box = PathSuggestionBoxRect();
            return new Rect(box.x + 1, box.y + 1 + index * SuggestionRowHeight,
                box.width - 2, SuggestionRowHeight);
        }

        /// <summary>
        /// Mouse interaction with the dropdown. Runs before the embedded browser so the
        /// clicks never reach the folder view underneath the overlay.
        /// </summary>
        void HandlePathSuggestionEvents()
        {
            if (!_editingPath || _pathSuggestions.Count == 0) return;
            var e = Event.current;
            var box = PathSuggestionBoxRect();
            if (e.type == EventType.MouseMove && box.Contains(e.mousePosition))
            {
                _pathSuggestionIndex = Mathf.Clamp(
                    (int)((e.mousePosition.y - box.y - 1) / SuggestionRowHeight),
                    0, _pathSuggestions.Count - 1);
                Repaint();
            }
            else if (e.type == EventType.MouseDown && box.Contains(e.mousePosition))
            {
                int row = Mathf.Clamp((int)((e.mousePosition.y - box.y - 1) / SuggestionRowHeight),
                    0, _pathSuggestions.Count - 1);
                var path = _pathSuggestions[row];
                e.Use();
                if (e.button == 2)
                {
                    // Middle-click: new tab, like everywhere else in the window.
                    _editingPath = false;
                    GUIUtility.keyboardControl = 0;
                    ClearPathSuggestions();
                    OpenInNewTab(path);
                }
                else
                {
                    CommitSuggestion(path);
                }
            }
        }

        /// <summary>Painted at the very end of OnGUI so it overlays the folder view.</summary>
        void DrawPathSuggestions()
        {
            if (!_editingPath || _pathSuggestions.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;
            var box = PathSuggestionBoxRect();
            var border = EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.4f, 0.4f, 0.4f);
            var background = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.9f, 0.9f, 0.9f);
            EditorGUI.DrawRect(box, border);
            EditorGUI.DrawRect(new Rect(box.x + 1, box.y + 1, box.width - 2, box.height - 2), background);
            for (int i = 0; i < _pathSuggestions.Count; i++)
            {
                var row = PathSuggestionRowRect(i);
                if (i == _pathSuggestionIndex)
                    EditorGUI.DrawRect(row, new Color(0.24f, 0.49f, 0.91f, 0.5f));
                var icon = AssetDatabase.GetCachedIcon(_pathSuggestions[i]);
                if (icon != null)
                    GUI.DrawTexture(new Rect(row.x + 3, row.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(row.x + 22, row.y, row.width - 24, row.height),
                    _pathSuggestions[i], EditorStyles.label);
            }
        }



    }
}
