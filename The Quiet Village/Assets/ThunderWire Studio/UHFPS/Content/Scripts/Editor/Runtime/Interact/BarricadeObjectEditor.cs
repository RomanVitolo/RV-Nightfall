using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;
using static UHFPS.Runtime.BarricadeObject;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(BarricadeObject)), CanEditMultipleObjects]
    public class BarricadeObjectEditor : InspectorEditor<BarricadeObject>
    {
        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying && Target.BreakStyle == EBreakStyle.Drag;
        }

        public override void OnInspectorGUI()
        {
            EditorDrawing.DrawInspectorHeader(new GUIContent("Barricade Object"), Target);
            EditorGUILayout.Space();

            serializedObject.Update();
            {
                DrawBreakStyleGroup();
                EditorDrawing.SeparatorSpaced(EditorGUIUtility.singleLineHeight);

                Properties.GetEnum("BreakStyle", out EBreakStyle breakStyle);

                if (Application.isPlaying && breakStyle == EBreakStyle.Drag)
                {
                    Rect progressRect = EditorGUILayout.GetControlRect();
                    float workPercent = Target.AccumulatedPullWorkValue / Target.RequiredPullWorkValue;
                    EditorGUI.ProgressBar(progressRect, workPercent, $"Work Value: {Target.AccumulatedPullWorkValue:F2}/{Target.RequiredPullWorkValue:F2}");

                    using (new EditorGUI.DisabledGroupScope(true))
                    {
                        EditorGUILayout.ToggleLeft("Is Unblocked", Target.IsUnblocked);
                    }

                    EditorGUILayout.Space();
                }

                using (new EditorDrawing.BorderBoxCleanScope(new GUIContent("Barricade Settings")))
                {
                    Properties.Draw("UnblockedLayer");
                    Properties.Draw("PullStrength");

                    if (breakStyle == EBreakStyle.Timed)
                    {
                        Properties.DrawBacking("InteractTime");
                    }
                }

                EditorGUILayout.Space();
                if (breakStyle == EBreakStyle.Drag)
                {
                    if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Custom Interact Icon"), Properties["UseCustomInteractIcon"]))
                    {
                        Properties.Draw("DisableReticleWhileHolding");
                        Properties.Draw("HoldIcon");
                        Properties.Draw("HoldSize");
                        EditorDrawing.EndBorderHeaderLayout();
                    }
                    
                    if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Keep Object In Hand After Unblock"), Properties["KeepObjectInHandAfterUnblock"]))
                    {
                        Properties.DrawBacking("MaxHoldDistanceValue", new GUIContent("Max Hold Distance"));
                        Properties.DrawBacking("ZoomDistanceValue", new GUIContent("Zoom Distance"));
                        EditorDrawing.EndBorderHeaderLayout();
                    }
                    
                    EditorGUILayout.Space();
                    
                    using (new EditorDrawing.BorderBoxCleanScope(new GUIContent("Drag Settings")))
                    {
                        Properties.Draw("RequiredPullWork");
                        Properties.Draw("PullEffort");
                        Properties.Draw("DragPullStrength");
                        Properties.Draw("PullResistance");
                        Properties.Draw("ReturnSmoothTime");
                        Properties.Draw("ResistanceMultiplier");
                        Properties.Draw("ReturnThreshold");
                        Properties.Draw("UseDragPointForForce");
                        Properties.Draw("SumPullForceWithDirection");
                        Properties.Draw("IgnorePlayerCollision");
                    }
                }

                EditorGUILayout.Space();
                using (new EditorDrawing.BorderBoxCleanScope(new GUIContent("Sound Settings")))
                {
                    Properties.Draw("CrackingAudioSource");
                    Properties.Draw("CrackingSounds", 1);
                    Properties.Draw("BreakSounds", 1);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Sound Volume Settings", EditorStyles.boldLabel);
                    Properties.Draw("MinCrackingTime");
                    Properties.Draw("CrackingMaxVolume");
                    Properties.Draw("BreakSoundVolume");
                }

                EditorGUILayout.Space();
                Properties.Draw("OnUnblocked");
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawBreakStyleGroup()
        {
            GUIContent[] toolbarContent =
            {
                new GUIContent("Instant", "Instantly unblocks the barricade object upon interaction."),
                new GUIContent("Timed",  "Unblocks the barricade object after a set interaction time."),
                new GUIContent("Drag",   "Unblocks the barricade object by dragging it with the mouse.")
            };

            using (new EditorDrawing.IconSizeScope(20))
            {
                GUIStyle toolbarButtons = new GUIStyle(GUI.skin.button)
                {
                    fixedHeight = 0,   // Let the layout decide height
                    fixedWidth = 70   // Each button 70px wide
                };

                // Get a line to draw on
                Rect toolbarRect = EditorGUILayout.GetControlRect(false, 25f);

                float toolbarWidth = toolbarButtons.fixedWidth * toolbarContent.Length;
                toolbarRect.width = toolbarWidth;

                toolbarRect.x = (EditorGUIUtility.currentViewWidth - toolbarWidth) * 0.5f;
                SerializedProperty breakStyleProp = Properties["BreakStyle"];

                int currentIndex = breakStyleProp.enumValueIndex;
                int newIndex = GUI.Toolbar(toolbarRect, currentIndex, toolbarContent, toolbarButtons);

                if (newIndex != currentIndex)
                {
                    Undo.RecordObject(target, "Change Break Style");

                    breakStyleProp.enumValueIndex = newIndex;
                    serializedObject.ApplyModifiedProperties();

                    EditorUtility.SetDirty(target);
                }
            }
        }

    }
}