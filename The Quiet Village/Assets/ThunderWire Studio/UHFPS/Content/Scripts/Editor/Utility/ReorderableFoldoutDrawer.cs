using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEditor;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    public abstract class ReorderableFoldoutDrawer : PropertyDrawer
    {
        private static readonly Dictionary<string, FoldableList> s_ListCache = new();

        protected FoldableList m_ReorderableList;
        protected List<CustomDropdownItem> m_Elements;

        protected SerializedProperty m_ListProperty;

        protected string m_InstanceNameProperty;
        protected string m_ListPropertyName;
        protected string m_elementsName;
        protected string m_miniMarkName;

        static ReorderableFoldoutDrawer()
        {
            EditorApplication.playModeStateChanged += _ => s_ListCache.Clear();
            EditorApplication.projectChanged += () => s_ListCache.Clear();
            EditorApplication.hierarchyChanged += () => s_ListCache.Clear();
            Selection.selectionChanged += () => s_ListCache.Clear();
        }

        private static string GetCacheKey(SerializedProperty property)
        {
            string instanceName = property.serializedObject.targetObject.name;
            return instanceName + "|" + property.propertyPath;
        }

        public void SetupDisplayInfo(string dropdownWindowName, string miniMarkName)
        {
            m_elementsName = dropdownWindowName;
            m_miniMarkName = miniMarkName;
        }

        public void InitializeElementList<T>(SerializedProperty listProperty, string instanceNameProperty = "Name") where T : class, ISerializedReferenceListItem
        {
            m_Elements = new();
            m_ListProperty = listProperty;
            m_ListPropertyName = null;
            m_InstanceNameProperty = instanceNameProperty;

            foreach (var type in TypeCache.GetTypesDerivedFrom<T>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition)
                    continue;

                T instance = (T)Activator.CreateInstance(type);
                var nameProp = type.GetProperty(instanceNameProperty);

                string name = nameProp != null ? (string)nameProp.GetValue(instance) : type.Name;
                m_Elements.Add(new CustomDropdownItem(name, type));
            }
        }

        public void InitializeElementList(Type type, SerializedProperty listProperty, string instanceNameProperty = "Name")
        {
            m_Elements = new();
            m_ListProperty = listProperty;
            m_ListPropertyName = null;
            m_InstanceNameProperty = instanceNameProperty;

            foreach (var t in TypeCache.GetTypesDerivedFrom(type))
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition)
                    continue;

                var instance = Activator.CreateInstance(t);
                var nameProp = t.GetProperty(instanceNameProperty);

                string name = nameProp != null ? (string)nameProp.GetValue(instance) : t.Name;
                m_Elements.Add(new CustomDropdownItem(name, t));
            }
        }

        public void InitializeElementList<T>(string listProperty, string instanceNameProperty = "Name") where T : class, ISerializedReferenceListItem
        {
            m_Elements = new();
            m_ListProperty = null;
            m_ListPropertyName = listProperty;
            m_InstanceNameProperty = instanceNameProperty;

            foreach (var type in TypeCache.GetTypesDerivedFrom<T>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition)
                    continue;

                T instance = (T)Activator.CreateInstance(type);
                var nameProp = type.GetProperty(instanceNameProperty);

                string name = nameProp != null ? (string)nameProp.GetValue(instance) : type.Name;
                m_Elements.Add(new CustomDropdownItem(name, type));
            }
        }

        public void InitializeElementList(Type type, string listProperty, string instanceNameProperty = "Name")
        {
            m_Elements = new();
            m_ListProperty = null;
            m_ListPropertyName = listProperty;
            m_InstanceNameProperty = instanceNameProperty;

            foreach (var t in TypeCache.GetTypesDerivedFrom(type))
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition)
                    continue;

                var instance = Activator.CreateInstance(t);
                var nameProp = t.GetProperty(instanceNameProperty);

                string name = nameProp != null ? (string)nameProp.GetValue(instance) : t.Name;
                m_Elements.Add(new CustomDropdownItem(name, t));
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SetupReorderableList(property, label);

            if (m_ReorderableList != null)
            {
                m_ReorderableList.BindProperty(GetElementsProperty(property));
                float listHeight = m_ReorderableList.GetFoldableHeight();
                Rect listRect = new(position.x, position.y, position.width, listHeight);
                m_ReorderableList.DoListFoldable(listRect);
            }
        }

        private void SetupReorderableList(SerializedProperty property, GUIContent label)
        {
            string key = GetCacheKey(property);
            if (s_ListCache.TryGetValue(key, out var cached))
            {
                m_ReorderableList = cached;
                return;
            }

            SerializedProperty elementsList = !string.IsNullOrEmpty(m_ListPropertyName)
                ? property.FindPropertyRelative(m_ListPropertyName)
                : m_ListProperty;

            if (elementsList == null)
                return;

            var list = new FoldableList(elementsList.serializedObject, elementsList, true, true, true, true);
            list.SetupFoldableList(label);
            list.OnDrawElement = DrawElementCallback;
            list.OnElementHeight = ElementHeightCallback;

            list.OnHeaderDrawn = (rect) =>
            {
                int count = elementsList.arraySize;
                string markName = string.IsNullOrEmpty(m_miniMarkName) ? count.ToString() : $"{count} {m_miniMarkName}";
                GUIContent markContent = new GUIContent(markName);
                float markX = EditorStyles.miniLabel.CalcSize(markContent).x;

                Rect markRect = new Rect(rect.x + rect.width - markX, rect.y, markX, rect.height);
                EditorGUI.LabelField(markRect, markContent, EditorStyles.miniLabel);
            };

            list.onAddDropdownCallback = (rect, _) =>
            {
                CustomDropdown.CreateAndShow(new CustomDropdownData()
                {
                    ContentRect = rect,
                    Name = m_elementsName,
                    Items = m_Elements,
                    Anchor = EDropdownAnchor.Right,
                }, (item) => AddElementToList(item));
            };

            s_ListCache[key] = list;
            m_ReorderableList = list;
        }

        protected virtual void AddElementToList(CustomDropdownItem item)
        {
            if (item.Item == null)
                return;

            var type = (Type)item.Item;
            var instance = Activator.CreateInstance(type);

            SerializedProperty modifiersList = m_ReorderableList.serializedProperty;
            var so = modifiersList.serializedObject;

            Undo.RecordObjects(so.targetObjects, "Add Element");
            so.Update();

            int newIndex = modifiersList.arraySize;
            modifiersList.arraySize++;

            SerializedProperty newElement = modifiersList.GetArrayElementAtIndex(newIndex);
            newElement.managedReferenceValue = instance;

            so.ApplyModifiedProperties();
            m_ReorderableList.ResetCache();
        }

        protected virtual void DrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty element = m_ReorderableList.serializedProperty.GetArrayElementAtIndex(index);
            if (element == null || element.managedReferenceValue == null)
                return;

            const float indentOffset = 12f;
            const float vspace = 2f;
            float y = rect.y + 1f;

            var instance = element.managedReferenceValue;
            Type elementType = instance.GetType();
            var nameProp = elementType.GetProperty(m_InstanceNameProperty);
            string displayName = elementType.Name;

            if(nameProp != null)
            {
                string nameValue = (string)nameProp.GetValue(instance);
                string[] groups = nameValue.Split('/');
                displayName = groups.Length > 0 ? groups[groups.Length - 1] : nameValue;
            }

            // Foldout header
            Rect foldoutRect = new(rect.x, y, rect.width, EditorGUIUtility.singleLineHeight);
            if (!EditorDrawing.IsInEditorWindow)
            {
                foldoutRect.x += indentOffset;
                foldoutRect.width -= indentOffset;
            }

            ShowPropertyClipboardMenu(foldoutRect, element);

            element.isExpanded = EditorGUI.Foldout(foldoutRect, element.isExpanded, displayName, true);
            y += EditorGUIUtility.singleLineHeight + vspace;

            if (element.isExpanded)
            {
                EditorGUI.indentLevel++;
                {
                    // check if there are child properties to draw
                    if (!element.hasVisibleChildren)
                    {
                        Rect labelRect = new(rect.x, y, rect.width, EditorGUIUtility.singleLineHeight);
                        EditorGUI.LabelField(labelRect, "No properties to show.", EditorDrawing.Styles.miniBoldItalicLabel);
                    }
                    else
                    {
                        SerializedProperty iterator = element.Copy();
                        SerializedProperty end = iterator.GetEndProperty();

                        bool enterChildren = true;
                        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
                        {
                            float h = EditorGUI.GetPropertyHeight(iterator, true);
                            Rect r = new Rect(rect.x, y, rect.width, h);
                            EditorGUI.PropertyField(r, iterator, true);
                            y += h + vspace;
                            enterChildren = false;
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }

        private static void ShowPropertyClipboardMenu(Rect rect, SerializedProperty element)
        {
            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                var menu = new GenericMenu();

                // Copy
                menu.AddItem(new GUIContent("Copy"), false, () =>
                {
                    ReferenceClipboardUtility.Copy(element);
                });

                // Paste (only if clipboard has a serialized property)
                if (ReferenceClipboardUtility.CanPaste())
                {
                    menu.AddItem(new GUIContent("Paste"), false, () =>
                    {
                    ReferenceClipboardUtility.Paste(element);
                    });
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Paste"));
                }

                menu.ShowAsContext();
                Event.current.Use();
            }
        }

        protected virtual float ElementHeightCallback(int index)
        {
            SerializedProperty element = m_ReorderableList.serializedProperty.GetArrayElementAtIndex(index);
            if (element == null || element.managedReferenceValue == null)
                return EditorGUIUtility.singleLineHeight;

            const float vspace = 2f;
            float height = EditorGUIUtility.singleLineHeight + vspace;

            if (element.isExpanded)
            {
                if (!element.hasVisibleChildren)
                {
                    // Reserve space for "No properties to show." label
                    height += EditorGUIUtility.singleLineHeight + vspace;
                }
                else
                {
                    SerializedProperty iterator = element.Copy();
                    SerializedProperty end = iterator.GetEndProperty();

                    bool enterChildren = true;
                    while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
                    {
                        height += EditorGUI.GetPropertyHeight(iterator, true) + vspace;
                        enterChildren = false;
                    }
                }
            }

            return height;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            SetupReorderableList(property, label ?? GUIContent.none);
            return m_ReorderableList != null ? m_ReorderableList.GetFoldableHeight() : EditorGUIUtility.singleLineHeight;
        }

        private SerializedProperty GetElementsProperty(SerializedProperty root)
        {
            return !string.IsNullOrEmpty(m_ListPropertyName)
                ? root.FindPropertyRelative(m_ListPropertyName)
                : root;
        }
    }

    [CustomPropertyDrawer(typeof(SerializedReferenceList<>), true)]
    public class SerializedReferenceListDrawer : ReorderableFoldoutDrawer
    {
        private bool _configured;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (!_configured)
            {
                var elementType = GetListElementType(fieldInfo.FieldType);
                if (elementType == null)
                {
                    Debug.LogError($"[SerializedReferenceListDrawer] Unable to determine the element type for field '{fieldInfo.Name}' in '{fieldInfo.DeclaringType}'.");
                    return;
                }

                SetupDisplayInfo("Items", null);
                InitializeElementList(elementType, "Items", "Name");

                _configured = true;
            }

            base.OnGUI(position, property, label);
        }

        private Type GetListElementType(Type t)
        {
            // Walk base types
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                if (cur.IsGenericType &&
                    cur.GetGenericTypeDefinition() == typeof(SerializedReferenceList<>))
                {
                    return cur.GetGenericArguments()[0];
                }
            }

            // (Optional) handle interface implementation cases
            foreach (var i in t.GetInterfaces())
            {
                if (i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(SerializedReferenceList<>))
                {
                    return i.GetGenericArguments()[0];
                }
            }

            return null;
        }
    }
}
