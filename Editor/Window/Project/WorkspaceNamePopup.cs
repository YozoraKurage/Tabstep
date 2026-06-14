using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>Tiny name prompt for "Save Tabs As..." — Unity has no built-in text dialog.</summary>
    internal sealed class WorkspaceNamePopup : PopupWindowContent
    {
        internal TabstepProjectWindow _owner;
        string _name = "";

        public override Vector2 GetWindowSize() => new Vector2(240, 58);

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.LabelField("Save tabs as workspace", EditorStyles.boldLabel);
            GUI.SetNextControlName("Tabstep.WorkspaceName");
            _name = EditorGUILayout.TextField(_name);
            EditorGUI.FocusTextInControl("Tabstep.WorkspaceName");
            bool submit = Event.current.type == EventType.KeyDown &&
                          (Event.current.keyCode == KeyCode.Return ||
                           Event.current.keyCode == KeyCode.KeypadEnter);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_name)))
                if (GUILayout.Button("Save") || (submit && !string.IsNullOrWhiteSpace(_name)))
                {
                    _owner.SaveWorkspace(_name);
                    editorWindow.Close();
                }
        }
    }
}
