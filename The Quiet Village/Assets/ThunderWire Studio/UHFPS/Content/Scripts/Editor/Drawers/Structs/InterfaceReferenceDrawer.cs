using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UHFPS.Runtime;

namespace UHFPS.Editors
{
    [CustomPropertyDrawer(typeof(InterfaceReference<>), true)]
    public class InterfaceReferenceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            {
                Type fieldType = fieldInfo.FieldType;
                Type interfaceType = fieldType.GetGenericArguments()[0];

                SerializedProperty objectProp = property.FindPropertyRelative("_object");

                // Draw the object field
                UnityEngine.Object current = objectProp.objectReferenceValue;
                label.text = $"{label.text} ({interfaceType.Name})";

                UnityEngine.Object newObj = EditorGUI.ObjectField(position, label, current, typeof(UnityEngine.Object), true);

                if (newObj != current)
                {
                    objectProp.objectReferenceValue = ValidateAndNormalizeObject(newObj, interfaceType);
                }
            }
            EditorGUI.EndProperty();
        }

        private UnityEngine.Object ValidateAndNormalizeObject(UnityEngine.Object obj, Type interfaceType)
        {
            if (obj == null)
                return null;

            // If it's a GameObject, try to find a component that implements the interface
            if (obj is GameObject go)
            {
                var matchingComponent = go.GetComponents<Component>()
                    .FirstOrDefault(c => c != null && interfaceType.IsAssignableFrom(c.GetType()));

                if (matchingComponent != null)
                    return matchingComponent;

                Debug.LogWarning($"GameObject '{go.name}' does not have a component that implements {interfaceType.Name}.");
                return null;
            }

            // If it's any UnityEngine.Object that implements the interface, accept it directly
            Type objType = obj.GetType();
            if (interfaceType.IsAssignableFrom(objType))
            {
                return obj;
            }

            Debug.LogWarning($"Object '{obj.name}' (type {objType.Name}) does not implement required interface {interfaceType.Name}.");
            return null;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }
}