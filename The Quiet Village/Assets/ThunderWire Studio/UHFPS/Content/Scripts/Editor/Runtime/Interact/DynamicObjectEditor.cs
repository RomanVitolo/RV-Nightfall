using UnityEditorInternal;
using UnityEditor;
using UnityEngine;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    [CustomEditor(typeof(DynamicObject))]
    public class DynamicObjectEditor : InspectorEditor<DynamicObject>
    {
        private ReorderableList ignoreCollidersList;

        // dynamic types
        private SerializedProperty openable;
        private PropertyCollection openableProperties;

        private SerializedProperty pullable;
        private PropertyCollection pullableProperties;

        private SerializedProperty switchable;
        private PropertyCollection switchableProperties;

        private SerializedProperty rotable;
        private PropertyCollection rotableProperties;

        private DynamicObject.DynamicType dynamicTypeEnum;
        private DynamicObject.InteractType interactTypeEnum;
        private DynamicObject.DynamicStatus dynamicStatusEnum;
        private DynamicObject.StatusChange statusChangeEnum;

        public override void OnEnable()
        {
            base.OnEnable();

            SerializedProperty ignoreColliders = Properties["ignoreColliders"];

            ignoreCollidersList = new ReorderableList(serializedObject, ignoreColliders, true, false, true, true);
            ignoreCollidersList.drawElementCallback += (rect, index, isActive, isFocused) =>
            {
                SerializedProperty element = ignoreColliders.GetArrayElementAtIndex(index);
                rect.y += EditorGUIUtility.standardVerticalSpacing;
                ReorderableList.defaultBehaviours.DrawElement(rect, element, null, isActive, isFocused, true, true);
            };

            // dynamic types
            {
                openable = serializedObject.FindProperty("openable");
                openableProperties = EditorDrawing.GetAllProperties(openable);

                pullable = serializedObject.FindProperty("pullable");
                pullableProperties = EditorDrawing.GetAllProperties(pullable);

                switchable = serializedObject.FindProperty("switchable");
                switchableProperties = EditorDrawing.GetAllProperties(switchable);

                rotable = serializedObject.FindProperty("rotable");
                rotableProperties = EditorDrawing.GetAllProperties(rotable);
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorDrawing.DrawInspectorHeader(new GUIContent("Dynamic Object"), Target);
            EditorGUILayout.Space();

            DrawDynamicTypeGroup();
            EditorGUILayout.Space();
            EditorDrawing.Separator();
            EditorGUILayout.Space();

            Properties.GetEnum("dynamicType", out dynamicTypeEnum);
            Properties.GetEnum("interactType", out interactTypeEnum);
            Properties.GetEnum("dynamicStatus", out dynamicStatusEnum);
            Properties.GetEnum("statusChange", out statusChangeEnum);

            using (new EditorDrawing.BorderBoxScope(false))
            {
                Properties.Draw("transformType");

                EditorGUI.BeginChangeCheck();
                Properties.Draw("interactType");
                if (EditorGUI.EndChangeCheck())
                {
                    Properties.GetEnum("interactType", out interactTypeEnum);
                    if (dynamicTypeEnum == DynamicObject.DynamicType.Openable && interactTypeEnum == DynamicObject.InteractType.Mouse)
                    {
                        Target.openable.bothSidesOpen = false;
                        Target.openable.flipOpenDirection = false;
                    }
                }

                Properties.Draw("dynamicStatus");

                if (dynamicStatusEnum != DynamicObject.DynamicStatus.Normal)
                    Properties.Draw("statusChange", new GUIContent("Unlock Condition"));

                if (Properties.BoolValue("isBarricaded"))
                {
                    EditorGUILayout.Space(2f);
                    EditorUtils.TrHelpIconText("This dynamic object is barricaded. Barricade script will determine whether the object is jammed or not.", MessageType.Info);
                }
            }

            if (dynamicStatusEnum == DynamicObject.DynamicStatus.Locked 
                && statusChangeEnum != DynamicObject.StatusChange.Manual)
            {
                EditorGUILayout.Space();

                using (new EditorDrawing.BorderBoxScope(new GUIContent("Status Change")))
                {
                    if (statusChangeEnum == DynamicObject.StatusChange.InventoryItem)
                    {
                        Properties.Draw("unlockItem", new GUIContent("Unlock Item"));
                    }
                    else if (statusChangeEnum == DynamicObject.StatusChange.CustomScript)
                    {
                        EditorGUILayout.Space(1f);
                        Properties.Draw("unlockScript");
                    }

                    if (statusChangeEnum == DynamicObject.StatusChange.InventoryItem)
                    {
                        Properties.Draw("keepUnlockItem");
                    }
                }
            }

            EditorGUILayout.Space();
            using (new EditorDrawing.BorderBoxScope(new GUIContent("Dynamic Settings")))
            {
                switch (dynamicTypeEnum)
                {
                    case DynamicObject.DynamicType.Openable:
                        DrawOpenableProperties();
                        break;
                    case DynamicObject.DynamicType.Pullable:
                        DrawPullableProperties();
                        break;
                    case DynamicObject.DynamicType.Switchable:
                        DrawSwitchableProperties();
                        break;
                    case DynamicObject.DynamicType.Rotable:
                        DrawRotableProperties();
                        break;
                }
            }

            EditorGUILayout.Space();
            if (EditorDrawing.BeginFoldoutBorderLayoutClean(Properties["useEvent1"], new GUIContent("Events")))
            {
                Properties.Draw("useEvent1", new GUIContent("OnOpen"));
                Properties.Draw("useEvent2", new GUIContent("OnClose"));

                if (interactTypeEnum != DynamicObject.InteractType.Animation)
                    Properties.Draw("onValueChange", new GUIContent("OnValueChange"));

                bool isLocked = dynamicStatusEnum == DynamicObject.DynamicStatus.Locked;
                bool isJammed = Properties.BoolValue("isBarricaded");
                
                if (isLocked)
                {
                    Properties.Draw("lockedEvent", new GUIContent("OnLocked"));
                }

                if (isLocked || isJammed)
                {
                    Properties.Draw("unlockedEvent", new GUIContent("OnUnlocked"));
                }

                EditorDrawing.EndBorderHeaderLayout();
            }

            serializedObject.ApplyModifiedProperties();
        }

        // --------------------------------------------------
        // DRAW DYNAMIC TYPE GROUP
        // --------------------------------------------------
        private void DrawDynamicTypeGroup()
        {
            const float kIconSize = 20f;
            const float kButtonWidth = 50f;
            const float kButtonHeight = 35f;

            GUIContent[] toolbarContent = {
                new(Resources.Load<Texture>("EditorIcons/icon_openable"), "Openable"),
                new(Resources.Load<Texture>("EditorIcons/icon_pullable"), "Pullable"),
                new(Resources.Load<Texture>("EditorIcons/icon_switchable"), "Switchable"),
                new(Resources.Load<Texture>("EditorIcons/icon_rotable"), "Rotable")
            };

            Vector2 prevIconSize = EditorGUIUtility.GetIconSize();
            EditorGUIUtility.SetIconSize(new Vector2(kIconSize, kIconSize));

            Rect toolbarRect = EditorGUILayout.GetControlRect(false, kButtonHeight);
            toolbarRect.width = kButtonWidth * toolbarContent.Length;
            toolbarRect.x = EditorGUIUtility.currentViewWidth / 2 - toolbarRect.width / 2 + 7f;

            GUIStyle toolbarButtons = new(GUI.skin.button)
            {
                fixedHeight = 0,
                fixedWidth = kButtonWidth
            };

            SerializedProperty dynamicType = Properties["dynamicType"];
            dynamicType.enumValueIndex = GUI.Toolbar(toolbarRect, dynamicType.enumValueIndex, toolbarContent, toolbarButtons);
            EditorGUIUtility.SetIconSize(prevIconSize);
        }

        // --------------------------------------------------
        // DRAW OPENABLE PROPERTIES
        // --------------------------------------------------
        private void DrawOpenableProperties()
        {
            if (interactTypeEnum == DynamicObject.InteractType.Mouse && openableProperties.BoolValue("dragSounds"))
                Properties.Draw("audioSource");

            switch (interactTypeEnum)
            {
                case DynamicObject.InteractType.Dynamic:
                    Properties.Draw("target");
                    break;
                case DynamicObject.InteractType.Mouse:
                    Properties.Draw("target");
                    Properties.Draw("joint");
                    Properties.Draw("rigidbody");
                    break;
                case DynamicObject.InteractType.Animation:
                    Properties.Draw("target");
                    Properties.Draw("animator");
                    break;
            }

            DrawLockedText();
            DrawJammedText();

            EditorGUILayout.Space();

            DrawBarricadeFoldout();

            if (interactTypeEnum != DynamicObject.InteractType.Animation)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(openableProperties["openLimits"], new GUIContent("Dynamic Limits")))
                {
                    openableProperties.Draw("openLimits");
                    DrawStartingAngle(openableProperties, "startingAngle", "openLimits");

                    EditorDrawing.SeparatorSpaced(2);

                    DrawLimitsAxis(openableProperties, "targetHinge");
                    DrawLimitsAxis(openableProperties, "targetForward");
                    openableProperties.Draw("useLocalAxes");

                    DrawInfo();
                    EditorDrawing.EndBorderHeaderLayout();
                }

                EditorGUILayout.Space(1f);
            }
            else
            {
                Target.openable.dragSounds = false;
                if (EditorDrawing.BeginFoldoutBorderLayout(openable, new GUIContent("Animation Settings")))
                {
                    Properties.Draw("useTrigger1", new GUIContent("Open Trigger Name"));
                    Properties.Draw("useTrigger2", new GUIContent("Close Trigger Name"));
                    if (openableProperties["bothSidesOpen"].boolValue)
                        Properties.Draw("useTrigger3", new GUIContent("OpenSide Name"));

                    EditorGUILayout.Space();
                    openableProperties.Draw("playCloseSound");

                    if (openableProperties.DrawGetBool("bothSidesOpen"))
                        openableProperties.Draw("frameForward", new GUIContent("Frame Forward"));

                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            if (interactTypeEnum == DynamicObject.InteractType.Dynamic)
            {
                Target.openable.dragSounds = false;
                if (EditorDrawing.BeginFoldoutBorderLayout(openable, new GUIContent("Dynamic Settings")))
                {
                    openableProperties.Draw("openSpeed");
                    openableProperties.Draw("openCurve");
                    openableProperties.Draw("closeCurve");

                    if (openableProperties.BoolValue("bothSidesOpen"))
                        openableProperties.Draw("frameForward", new GUIContent("Frame Forward"));

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Properties", EditorStyles.boldLabel);
                    openableProperties.Draw("flipOpenDirection");
                    openableProperties.Draw("useUpward");
                    openableProperties.Draw("bothSidesOpen");
                    openableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }
            else if (interactTypeEnum == DynamicObject.InteractType.Mouse)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(openable, new GUIContent("Mouse Settings")))
                {
                    openableProperties.Draw("openSpeed");
                    openableProperties.Draw("damper");
                    if (openableProperties["dragSounds"].boolValue)
                        openableProperties.Draw("dragSoundPlay");
                    EditorGUILayout.Space();

                    openableProperties.Draw("dragSounds");
                    openableProperties.Draw("flipMouse");
                    openableProperties.Draw("flipAngle");
                    openableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(openableProperties["useLockedMotion"], new GUIContent("Locked Settings")))
            {
                openableProperties.Draw("useLockedMotion");
                openableProperties.Draw("lockedPattern");
                openableProperties.Draw("lockedMotionAmount");
                openableProperties.Draw("lockedMotionTime");
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["ignoreColliders"], new GUIContent("Ignore Colliders")))
            {
                ignoreCollidersList.DoLayoutList();
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["useSound1"], new GUIContent("Dynamic Sounds")))
            {
                if (openableProperties["dragSounds"].boolValue)
                {
                    openableProperties.Draw("dragSound");
                    EditorGUILayout.Space(2f);
                }

                Properties.Draw("useSound1", new GUIContent("Open Sound"));
                Properties.Draw("useSound2", new GUIContent("Close Sound"));

                if (dynamicStatusEnum == DynamicObject.DynamicStatus.Locked)
                {
                    Properties.Draw("unlockSound");
                    Properties.Draw("lockedSound");
                }
                EditorDrawing.EndBorderHeaderLayout();
            }
        }

        // --------------------------------------------------
        // DRAW PULLABLE PROPERTIES
        // --------------------------------------------------
        private void DrawPullableProperties()
        {
            if (interactTypeEnum == DynamicObject.InteractType.Mouse && pullableProperties.BoolValue("dragSounds"))
                Properties.Draw("audioSource");

            switch (interactTypeEnum)
            {
                case DynamicObject.InteractType.Dynamic:
                case DynamicObject.InteractType.Mouse:
                    Properties.Draw("target");
                    break;
                case DynamicObject.InteractType.Animation:
                    Properties.Draw("animator");
                    break;
            }

            DrawLockedText();
            DrawJammedText();

            EditorGUILayout.Space();

            DrawBarricadeFoldout();

            if (interactTypeEnum != DynamicObject.InteractType.Animation)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(pullableProperties["openLimits"], new GUIContent("Dynamic Limits")))
                {
                    pullableProperties.Draw("openLimits");
                    pullableProperties.Draw("pullAxis");
                    EditorDrawing.EndBorderHeaderLayout();
                }

                EditorGUILayout.Space(1f);
            }
            else
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(pullable, new GUIContent("Animation Settings")))
                {
                    Properties.Draw("useTrigger1", new GUIContent("Open Trigger Name"));
                    Properties.Draw("useTrigger2", new GUIContent("Close Trigger Name"));
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            if (interactTypeEnum == DynamicObject.InteractType.Dynamic)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(pullable, new GUIContent("Dynamic Settings")))
                {
                    pullableProperties.Draw("openCurve");
                    pullableProperties.Draw("openSpeed");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }
            else if (interactTypeEnum == DynamicObject.InteractType.Mouse)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(pullable, new GUIContent("Mouse Settings")))
                {
                    pullableProperties.Draw("openSpeed");
                    pullableProperties.Draw("damping");
                    if (pullableProperties["dragSounds"].boolValue)
                        pullableProperties.Draw("dragSoundPlay");
                    EditorGUILayout.Space();

                    pullableProperties.Draw("dragSounds");
                    pullableProperties.Draw("flipMouse");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["ignoreColliders"], new GUIContent("Ignore Colliders")))
            {
                Properties.Draw("ignorePlayerCollider");
                ignoreCollidersList.DoLayoutList();
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["useSound1"], new GUIContent("Dynamic Sounds")))
            {
                Properties.Draw("useSound1", new GUIContent("Open Sound"));
                Properties.Draw("useSound2", new GUIContent("Close Sound"));

                if (dynamicStatusEnum == DynamicObject.DynamicStatus.Locked)
                {
                    Properties.Draw("unlockSound");
                    Properties.Draw("lockedSound");
                }
                EditorDrawing.EndBorderHeaderLayout();
            }
        }

        // --------------------------------------------------
        // DRAW SWITCHABLE PROPERTIES
        // --------------------------------------------------
        private void DrawSwitchableProperties()
        {
            switch (interactTypeEnum)
            {
                case DynamicObject.InteractType.Dynamic:
                case DynamicObject.InteractType.Mouse:
                    Properties.Draw("target");
                    break;
                case DynamicObject.InteractType.Animation:
                    Properties.Draw("animator");
                    break;
            }

            DrawLockedText();
            DrawJammedText();

            EditorGUILayout.Space();

            DrawBarricadeFoldout();

            if (interactTypeEnum != DynamicObject.InteractType.Animation)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(switchableProperties["switchLimits"], new GUIContent("Dynamic Limits")))
                {
                    switchableProperties.Draw("switchLimits");
                    DrawStartingAngle(switchableProperties, "startingAngle", "switchLimits");

                    EditorDrawing.SeparatorSpaced(2);

                    DrawLimitsAxis(switchableProperties, "targetHinge");
                    DrawLimitsAxis(switchableProperties, "targetForward");
                    switchableProperties.Draw("useLocalAxes");

                    DrawInfo();
                    EditorDrawing.EndBorderHeaderLayout();
                }

                EditorGUILayout.Space(1f);
            }
            else
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(switchable, new GUIContent("Animation Settings")))
                {
                    Properties.Draw("useTrigger1", new GUIContent("SwitchOn Trigger Name"));
                    Properties.Draw("useTrigger2", new GUIContent("SwitchOff Trigger Name"));
                    EditorGUILayout.Space();

                    switchableProperties.Draw("lockOnSwitch");

                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            if (interactTypeEnum == DynamicObject.InteractType.Dynamic)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(switchable, new GUIContent("Dynamic Settings")))
                {
                    switchableProperties.Draw("rootObject");
                    switchableProperties.Draw("switchOnCurve");
                    switchableProperties.Draw("switchOffCurve");
                    switchableProperties.Draw("switchSpeed");
                    EditorGUILayout.Space();

                    switchableProperties.Draw("flipSwitchDirection");
                    switchableProperties.Draw("lockOnSwitch");
                    switchableProperties.Draw("flipAngle");
                    switchableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }
            else if (interactTypeEnum == DynamicObject.InteractType.Mouse)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(switchable, new GUIContent("Mouse Settings")))
                {
                    switchableProperties.Draw("rootObject");
                    switchableProperties.Draw("switchSpeed");
                    switchableProperties.Draw("damping");
                    EditorGUILayout.Space();

                    switchableProperties.Draw("lockOnSwitch");
                    switchableProperties.Draw("flipMouse");
                    switchableProperties.Draw("flipAngle");
                    switchableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["ignoreColliders"], new GUIContent("Ignore Colliders")))
            {
                ignoreCollidersList.DoLayoutList();
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["useSound1"], new GUIContent("Dynamic Sounds")))
            {
                Properties.Draw("useSound1", new GUIContent("SwitchUp Sound"));
                Properties.Draw("useSound2", new GUIContent("SwitchDown Sound"));

                if (dynamicStatusEnum == DynamicObject.DynamicStatus.Locked)
                {
                    Properties.Draw("unlockSound");
                    Properties.Draw("lockedSound");
                }
                EditorDrawing.EndBorderHeaderLayout();
            }
        }

        // --------------------------------------------------
        // DRAW ROTABLE PROPERTIES
        // --------------------------------------------------
        private void DrawRotableProperties()
        {
            switch (interactTypeEnum)
            {
                case DynamicObject.InteractType.Dynamic:
                case DynamicObject.InteractType.Mouse:
                    Properties.Draw("target");
                    break;
                case DynamicObject.InteractType.Animation:
                    Properties.Draw("animator");
                    break;
            }

            DrawLockedText();
            DrawJammedText();

            EditorGUILayout.Space();

            DrawBarricadeFoldout();

            if (interactTypeEnum != DynamicObject.InteractType.Animation)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(rotableProperties["rotationLimit"], new GUIContent("Dynamic Limits")))
                {
                    rotableProperties.Draw("rotationLimit");
                    rotableProperties.Draw("rotateAroundAxis");
                    EditorDrawing.EndBorderHeaderLayout();
                }

                EditorGUILayout.Space(1f);
            }
            else
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(rotable, new GUIContent("Animation Settings")))
                {
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            if (interactTypeEnum == DynamicObject.InteractType.Dynamic)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(rotable, new GUIContent("Dynamic Settings")))
                {
                    rotableProperties.Draw("rotateCurve");
                    rotableProperties.Draw("rotationSpeed");
                    EditorGUILayout.Space();

                    rotableProperties.Draw("holdToRotate");
                    rotableProperties.Draw("lockOnRotate");
                    Properties.Draw("lockPlayer");
                    rotableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }
            else if (interactTypeEnum == DynamicObject.InteractType.Mouse)
            {
                if (EditorDrawing.BeginFoldoutBorderLayout(rotable, new GUIContent("Mouse Settings")))
                {
                    rotableProperties.Draw("rotationSpeed");
                    rotableProperties.Draw("mouseMultiplier");
                    rotableProperties.Draw("damping");
                    EditorGUILayout.Space();

                    rotableProperties.Draw("lockOnRotate");
                    rotableProperties.Draw("showGizmos");
                    EditorDrawing.EndBorderHeaderLayout();
                }
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["ignoreColliders"], new GUIContent("Ignore Colliders")))
            {
                ignoreCollidersList.DoLayoutList();
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
            if (EditorDrawing.BeginFoldoutBorderLayout(Properties["useSound1"], new GUIContent("Dynamic Sounds")))
            {
                Properties.Draw("useSound1", new GUIContent("RotateUp Sound"));
                Properties.Draw("useSound2", new GUIContent("RotateDown Sound"));

                if (dynamicStatusEnum == DynamicObject.DynamicStatus.Locked)
                {
                    Properties.Draw("unlockSound");
                    Properties.Draw("lockedSound");
                }
                EditorDrawing.EndBorderHeaderLayout();
            }
        }

        // --------------------------------------------------
        // PROPERTY DRAWING METHODS
        // --------------------------------------------------
        private void DrawLockedText()
        {
            if (dynamicStatusEnum != DynamicObject.DynamicStatus.Locked)
                return;

            Rect lockedTextRect = EditorGUILayout.GetControlRect();
            lockedTextRect = EditorGUI.PrefixLabel(lockedTextRect, new GUIContent("Locked Text"));

            Properties.Draw(lockedTextRect, "showLockedText", GUIContent.none);
            lockedTextRect.xMin += 20f;

            using (new EditorGUI.DisabledScope(!Properties.BoolValue("showLockedText")))
            {
                Properties.Draw(lockedTextRect, "lockedText", GUIContent.none);
            }
        }
        
        private void DrawJammedText()
        {
            if (!Properties.BoolValue("isBarricaded"))
                return;

            Rect jammedTextRect = EditorGUILayout.GetControlRect();
            jammedTextRect = EditorGUI.PrefixLabel(jammedTextRect, new GUIContent("Jammed Text"));

            Properties.Draw(jammedTextRect, "showJammedText", GUIContent.none);
            jammedTextRect.xMin += 20f;

            using (new EditorGUI.DisabledScope(!Properties.BoolValue("showJammedText")))
            {
                Properties.Draw(jammedTextRect, "jammedText", GUIContent.none);
            }
        }

        private void DrawBarricadeFoldout()
        {
            if (EditorDrawing.BeginFoldoutToggleBorderLayout(new GUIContent("Barricade"), Properties["isBarricaded"]))
            {
                Properties.Draw("barricadeScript");
                EditorDrawing.EndBorderHeaderLayout();
            }

            EditorGUILayout.Space(1f);
        }

        // --------------------------------------------------
        // UTILITIES
        // --------------------------------------------------
        private void DrawStartingAngle(PropertyCollection properties, string propertyName, string limitsName)
        {
            Rect rect = EditorGUILayout.GetControlRect();

            SerializedProperty angleProperty = properties[propertyName];
            SerializedProperty angleFlip = properties[propertyName + "Flip"];

            float spacing = EditorGUIUtility.standardVerticalSpacing;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float propertyFieldWidth = rect.width - (lineHeight * 2) - (spacing * 3);

            float minLimit = properties[limitsName].FindPropertyRelative("min").floatValue;
            float maxLimit = properties[limitsName].FindPropertyRelative("max").floatValue;

            Rect propertyFieldRect = new(rect.x, rect.y, propertyFieldWidth, lineHeight);
            angleProperty.floatValue = EditorGUI.Slider(propertyFieldRect, new GUIContent(angleProperty.displayName), angleProperty.floatValue, minLimit, maxLimit);

            Rect iconRect = new(propertyFieldRect.xMax + spacing, rect.y, lineHeight, lineHeight);
            GUIContent mirrorIcon = EditorGUIUtility.TrIconContent("Mirror");
            EditorGUI.LabelField(iconRect, mirrorIcon);

            Rect toggleRect = new(iconRect.xMax + spacing, rect.y, lineHeight, lineHeight);
            angleFlip.boolValue = EditorGUI.Toggle(toggleRect, angleFlip.boolValue);
        }

        private void DrawLimitsAxis(PropertyCollection properties, string propertyName)
        {
            Rect rect = EditorGUILayout.GetControlRect();

            SerializedProperty limitsMirror = properties[propertyName + "Mirror"];

            float spacing = EditorGUIUtility.standardVerticalSpacing;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float propertyFieldWidth = rect.width - (lineHeight * 2) - (spacing * 3);

            Rect propertyFieldRect = new(rect.x, rect.y, propertyFieldWidth, lineHeight);
            properties.Draw(propertyFieldRect, propertyName);

            Rect iconRect = new(propertyFieldRect.xMax + spacing, rect.y, lineHeight, lineHeight);
            GUIContent mirrorIcon = EditorGUIUtility.TrIconContent("Mirror");
            EditorGUI.LabelField(iconRect, mirrorIcon);

            Rect toggleRect = new(iconRect.xMax + spacing, rect.y, lineHeight, lineHeight);
            limitsMirror.boolValue = EditorGUI.Toggle(toggleRect, limitsMirror.boolValue);
        }

        private void DrawInfo()
        {
            bool flag1 = UnityEditor.Tools.pivotMode == PivotMode.Center;
            bool flag2 = UnityEditor.Tools.pivotRotation == PivotRotation.Global;
            if (flag1 || flag2) EditorGUILayout.Space();

            using (new EditorDrawing.BackgroundColorScope("#F78787"))
            {
                if (flag1)
                {
                    EditorUtils.TrHelpIconText("Object pivot mode is set to center. Change it to pivot to get the pivot position of the object.", MessageType.Warning);
                }
                if (flag2)
                {
                    EditorUtils.TrHelpIconText("Pivot rotation mode is set to global. Change it to local to get the local orientation of the object.", MessageType.Warning);
                }
            }

            if ((flag1 || flag2) && GUILayout.Button("Fix"))
            {
                UnityEditor.Tools.pivotMode = PivotMode.Pivot;
                UnityEditor.Tools.pivotRotation = PivotRotation.Local;
            }
        }
    }
}
