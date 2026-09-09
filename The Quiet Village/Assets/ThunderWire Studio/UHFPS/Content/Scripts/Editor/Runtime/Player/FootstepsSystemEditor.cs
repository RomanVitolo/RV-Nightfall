using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(FootstepsSystem))]
    public class FootstepsSystemEditor : InspectorEditor<FootstepsSystem>
    {
        public override void OnInspectorGUI()
        {
            EditorDrawing.DrawInspectorHeader(new GUIContent("Footsteps System"), Target);
            EditorGUILayout.Space();

            serializedObject.Update();
            {
                Properties.Draw("SurfaceDefinitionSet");
                Properties.Draw("FootstepStyle");
                Properties.Draw("SurfaceDetection");
                Properties.Draw("FootstepsMask");

                EditorGUILayout.Space();
                using(new EditorDrawing.BorderBoxScope(new GUIContent("Footstep Settings")))
                {
                    Properties.Draw("StepPlayerVelocity");
                    Properties.Draw("JumpStepAirTime");

                    if (Target.FootstepStyle == FootstepsSystem.FootstepStyleEnum.HeadBob)
                    {
                        Properties.Draw("HeadBobStepWave");
                    }
                }

                bool isTimed = Target.FootstepStyle == FootstepsSystem.FootstepStyleEnum.Timed;

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Footstep Types", EditorStyles.boldLabel);

                if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Crouch Steps"), Properties["EnableCrouchSteps"]))
                {
                    using (new EditorGUI.DisabledGroupScope(!Properties.BoolValue("EnableCrouchSteps")))
                    {
                        using (new EditorGUI.DisabledGroupScope(!isTimed))
                        {
                            Properties.Draw("CrouchStepTime");
                        }

                        Properties.Draw("CrouchingVolume");
                    }

                    EditorDrawing.EndBorderHeaderLayout();
                }

                if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Walk Steps"), Properties["EnableWalkSteps"]))
                {
                    using (new EditorGUI.DisabledGroupScope(!Properties.BoolValue("EnableWalkSteps")))
                    {
                        using (new EditorGUI.DisabledGroupScope(!isTimed))
                        {
                            Properties.Draw("WalkStepTime");
                        }

                        Properties.Draw("WalkingVolume");
                    }
                    EditorDrawing.EndBorderHeaderLayout();
                }

                if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Run Steps"), Properties["EnableRunSteps"]))
                {
                    using (new EditorGUI.DisabledGroupScope(!Properties.BoolValue("EnableRunSteps")))
                    {
                        using (new EditorGUI.DisabledGroupScope(!isTimed))
                        {
                            Properties.Draw("RunStepTime");
                        }

                        Properties.Draw("RunningVolume");
                    }
                    EditorDrawing.EndBorderHeaderLayout();
                }

                if (EditorDrawing.BeginFoldoutToggleBorderLayoutClean(new GUIContent("Land Steps"), Properties["EnableLandSteps"]))
                {
                    using (new EditorGUI.DisabledGroupScope(!Properties.BoolValue("EnableLandSteps")))
                    {
                        using (new EditorGUI.DisabledGroupScope(!isTimed))
                        {
                            Properties.Draw("LandStepTime");
                        }

                        Properties.Draw("LandVolume");
                    }
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}