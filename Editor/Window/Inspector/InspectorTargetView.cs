// Parked: the tabbed inspector is temporarily withdrawn from the Unity UI while its
// integration strategy is reworked in a separate scope. Add TABSTEP_INSPECTOR to the
// project's Scripting Define Symbols to bring it back.
#if TABSTEP_INSPECTOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Renders a full inspector for one object, independent of the global selection.
    /// Uses an <see cref="ActiveEditorTracker"/> locked to the target — the same
    /// machinery as Unity's Inspector window, so GameObjects get their component
    /// list and assets get their importer editors. Locking the tracker is internal
    /// API; when unavailable this degrades to a single Editor.CreateEditor view.
    /// </summary>
    internal sealed class InspectorTargetView : IDisposable
    {
        // internal void ActiveEditorTracker.SetObjectsLockedByThisTracker(List<Object>)
        static readonly MethodInfo SetObjectsLockedMethod = typeof(ActiveEditorTracker)
            .GetMethod("SetObjectsLockedByThisTracker", BindingFlags.Instance | BindingFlags.NonPublic);

        Object _target;
        ActiveEditorTracker _tracker;
        Editor _fallbackEditor;

        public void Dispose()
        {
            Release();
            _target = null;
        }

        void Release()
        {
            _tracker?.Destroy();
            _tracker = null;
            if (_fallbackEditor != null) Object.DestroyImmediate(_fallbackEditor);
            _fallbackEditor = null;
        }

        public void SetTarget(Object target)
        {
            // After a domain reload the editors are gone even when the target survived.
            if (target == _target && (_tracker != null || _fallbackEditor != null || target == null))
                return;
            _target = target;
            Release();
            if (_target == null) return;

            if (SetObjectsLockedMethod != null)
            {
                _tracker = new ActiveEditorTracker();
                try
                {
                    SetObjectsLockedMethod.Invoke(_tracker, new object[] { new List<Object> { _target } });
                    _tracker.ForceRebuild();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Tabstep] Could not lock the editor tracker: {e.InnerException ?? e}");
                    _tracker.Destroy();
                    _tracker = null;
                }
            }
            if (_tracker == null)
                _fallbackEditor = Editor.CreateEditor(_target);
        }

        /// <summary>Inspector-window-style 10 Hz maintenance; call from OnInspectorUpdate.</summary>
        public void Update()
        {
            _tracker?.VerifyModifiedMonoBehaviours();
        }

        public void OnGUI(float width)
        {
            if (_target == null)
            {
                EditorGUILayout.HelpBox("The object of this tab was deleted (or its scene was closed).",
                    MessageType.Info);
                return;
            }

            EditorGUIUtility.wideMode = width > 330f;
            if (_tracker != null) DrawTracked();
            else if (_fallbackEditor != null) DrawSingle(_fallbackEditor);
        }

        void DrawTracked()
        {
            var editors = _tracker.activeEditors;
            for (int i = 0; i < editors.Length; i++)
            {
                var editor = editors[i];
                if (editor == null || editor.target == null) continue;
                if (i == 0)
                {
                    editor.DrawHeader();
                    // The GameObject header editor has no meaningful body; its content
                    // is the component editors that follow.
                    if (!(editor.target is GameObject))
                        DrawBody(editor);
                    continue;
                }

                bool visible = _tracker.GetVisible(i) != 0; // -1 (unset) renders expanded
                bool nowVisible = EditorGUILayout.InspectorTitlebar(visible, editor);
                if (nowVisible != visible)
                    _tracker.SetVisible(i, nowVisible ? 1 : 0);
                if (nowVisible)
                    DrawBody(editor);
            }
        }

        void DrawSingle(Editor editor)
        {
            editor.DrawHeader();
            DrawBody(editor);
        }

        static void DrawBody(Editor editor)
        {
            using (new EditorGUILayout.VerticalScope(
                       editor.UseDefaultMargins() ? EditorStyles.inspectorDefaultMargins : GUIStyle.none))
            {
                editor.OnInspectorGUI();
            }
        }
    }
}
#endif
