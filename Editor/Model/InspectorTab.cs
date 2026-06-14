// Parked: the tabbed inspector is temporarily withdrawn from the Unity UI while its
// integration strategy is reworked in a separate scope. Add TABSTEP_INSPECTOR to the
// project's Scripting Define Symbols to bring it back.
#if TABSTEP_INSPECTOR
using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// One Tabstep Inspector tab. Either locked to one object, or — for the pinned
    /// "Selection" tab that lets the window stand in for the stock Inspector —
    /// following whatever is currently selected.
    /// Serialized into the window — asset tabs survive reloads and restarts,
    /// scene-object tabs live as long as their scene is open.
    /// </summary>
    [Serializable]
    internal sealed class InspectorTab
    {
        [SerializeField] Object _target;
        [SerializeField] bool _followsSelection;
        [SerializeField] Vector2 _scroll;

        public InspectorTab() { }

        public InspectorTab(Object target)
        {
            _target = target;
        }

        public static InspectorTab CreateSelectionTab()
        {
            return new InspectorTab { _followsSelection = true };
        }

        public bool FollowsSelection => _followsSelection;
        public Object Target => _followsSelection ? Selection.activeObject : _target;
        public bool IsAlive => Target != null;

        public Vector2 Scroll
        {
            get => _scroll;
            set => _scroll = value;
        }

        public string DisplayName
        {
            get
            {
                if (_followsSelection) return "Selection";
                return _target != null ? _target.name : "(missing)";
            }
        }

        /// <summary>Duplicating the Selection tab freezes what it currently shows into a locked tab.</summary>
        public InspectorTab Clone()
        {
            return new InspectorTab(Target);
        }
    }
}
#endif
