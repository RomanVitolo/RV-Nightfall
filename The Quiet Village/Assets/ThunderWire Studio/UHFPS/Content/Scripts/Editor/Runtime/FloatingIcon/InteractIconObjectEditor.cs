using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(InteractIconObject)), CanEditMultipleObjects]
    public class InteractIconObjectEditor : InspectorEditor<InteractIconObject>
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.HelpBox("An object with this script attached will be marked as a floating icon object, so that when you hover mouse over the object, the floating icon will appear.", MessageType.Info);
            EditorGUILayout.HelpBox("If you hold down the use button, the icon will change to a hold icon and back when you release the use button.", MessageType.Info);
            EditorGUILayout.Space();

            serializedObject.Update();
            {
                using (new EditorDrawing.BorderBoxCleanScope(new GUIContent("Settings")))
                {
                    Properties.Draw("UseTransformOffset");

                    if (Target.UseTransformOffset)
                    {
                        Properties.Draw("IconTransform");
                    }
                    else
                    {
                        Properties.Draw("IconOffset");
                    }
                }

                EditorGUILayout.Space();

                using (new EditorDrawing.BorderBoxCleanScope(new GUIContent("Icons")))
                {
                    using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                    {
                        Properties.Draw("HoverIcon");
                        Properties.Draw("HoverSize");
                    }

                    using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                    {
                        Properties.Draw("HoldIcon");
                        Properties.Draw("HoldSize");
                    }
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}