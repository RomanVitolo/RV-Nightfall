using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(UVFlashlightReveal))]
    public class UVFlashlightRevealEditor : InspectorEditor<UVFlashlightReveal>
    {
        public override void OnInspectorGUI()
        {
            EditorDrawing.DrawInspectorHeader(new GUIContent("UV Flashlight Reveal"), Target);
            EditorGUILayout.Space();

            serializedObject.Update();
            {
                Properties.Draw("MaterialSource");
                
                switch (Target.MaterialSource)
                {
                    case UVFlashlightReveal.EMaterialSource.DecalProjector:
                        Properties.Draw("DecalProjector");
                        break;
                    case UVFlashlightReveal.EMaterialSource.RendererMaterial:
                        Properties.Draw("RendererMaterial");
                        break;
                    case UVFlashlightReveal.EMaterialSource.CustomMaterial:
                        Properties.Draw("CustomMaterial");
                        break;
                }
                
                EditorGUILayout.Space();
                
                using (new EditorDrawing.ToggleBorderBoxScope(new GUIContent("Reveal Event"), Properties["EnableRevealEvent"]))
                {
                    Properties.Draw("RectOffset");
                    Properties.Draw("RectSize");
                    Properties.Draw("RevealThreshold");
                    Properties.Draw("UseOcclusionRaycast");
                    Properties.Draw("OcclusionMask");
                }

                if (Target.RevealMaterial != null)
                {
                    EditorGUILayout.Space();

                    bool hasPositionProperty = Target.RevealMaterial.HasProperty(UVFlashlightReveal.UVLightPosition);
                    bool hasDirectionProperty = Target.RevealMaterial.HasProperty(UVFlashlightReveal.UVLightDirection);
                    if (hasPositionProperty && hasDirectionProperty)
                    {
                        if (EditorDrawing.BeginFoldoutBorderLayoutClean(Properties["OnRevealed"], new GUIContent("Hidden Properties")))
                        {
                            using (new EditorGUI.DisabledScope(true))
                            {
                                Vector3 lightPosition =
                                    Target.RevealMaterial.GetVector(UVFlashlightReveal.UVLightPosition);
                                Vector3 lightDirection =
                                    Target.RevealMaterial.GetVector(UVFlashlightReveal.UVLightDirection);
                                float lightAngle = Target.RevealMaterial.GetFloat(UVFlashlightReveal.UVLightAngle);
                                float lightRange = Target.RevealMaterial.GetFloat(UVFlashlightReveal.UVLightRange);
                                float intensity = Target.RevealMaterial.GetFloat(UVFlashlightReveal.UVLightIntensity);

                                EditorGUILayout.Vector3Field("Light Position", lightPosition);
                                EditorGUILayout.Vector3Field("Light Direction", lightDirection);
                                EditorGUILayout.FloatField("Light Angle", lightAngle);
                                EditorGUILayout.FloatField("Light Range", lightRange);
                                EditorGUILayout.FloatField("Light Intensity", intensity);
                            }
                            
                            EditorGUILayout.Space();
                            if (GUILayout.Button("Reset Reveal Data"))
                            {
                                Target.ResetLightData();
                            }

                            if (GUILayout.Button("Clear Reveal Data"))
                            {
                                Target.ClearLightData();
                            }
                            
                            EditorDrawing.EndBorderHeaderLayout();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Reveal Material is missing required properties. Make sure it has the following properties:\n" +
                            "_UVLightPositionWS (Vector)\n" +
                            "_UVLightDirectionWS (Vector)\n" +
                            "_UVLightAngle (Float)\n" +
                            "_UVLightRange (Float)", MessageType.Error);
                    }
                }

                if (Target.EnableRevealEvent)
                {
                    EditorGUILayout.Space();
                    Properties.Draw("OnRevealed");
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}