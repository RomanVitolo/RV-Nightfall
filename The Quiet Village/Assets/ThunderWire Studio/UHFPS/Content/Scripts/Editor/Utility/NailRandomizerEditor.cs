using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(NailRandomizer)), CanEditMultipleObjects]
    public class NailRandomizerEditor : InspectorEditor<NailRandomizer>
    {
        public override void OnInspectorGUI()
        {
            EditorDrawing.DrawInspectorHeader(new GUIContent("Nail Randomizer"), Target);
            EditorGUILayout.Space();

            serializedObject.Update();
            {
                DrawPropertiesExcluding(serializedObject, "m_Script");

                EditorGUILayout.Space();
                if (GUILayout.Button("Randomize", GUILayout.Height(30)))
                {
                    RandomizeAllSelected();
                }
                
                if (GUILayout.Button("Reset Rotation", GUILayout.Height(20)))
                {
                    foreach (var obj in targets)
                    {
                        var nail = obj as NailRandomizer;
                        if (nail == null) continue;

                        Undo.RecordObject(nail.transform, "Reset Nail Rotation");
                        nail.ResetRotation();
                        EditorUtility.SetDirty(nail.transform);
                    }
                }
                
                if (GUILayout.Button("Reset Direction", GUILayout.Height(20)))
                {
                    foreach (var obj in targets)
                    {
                        var nail = obj as NailRandomizer;
                        if (nail == null) continue;

                        Undo.RecordObject(nail, "Reset Nail Direction");
                        nail.ResetDirection();
                        EditorUtility.SetDirty(nail);
                    }
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void RandomizeAllSelected()
        {
            foreach (var obj in targets)
            {
                var nail = obj as NailRandomizer;
                if (nail == null) continue;

                Undo.RecordObject(nail.transform, "Randomize Nail");
                var mf = nail.GetComponent<MeshFilter>();
                var mr = nail.GetComponent<MeshRenderer>();
                if (mf) Undo.RecordObject(mf, "Randomize Nail");
                if (mr) Undo.RecordObject(mr, "Randomize Nail");

                nail.Randomize();

                EditorUtility.SetDirty(nail);
                if (mf) EditorUtility.SetDirty(mf);
                if (mr) EditorUtility.SetDirty(mr);
            }
        }
    }
}