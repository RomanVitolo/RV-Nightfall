using System;
using System.Collections;
using System.Reflection;
using ThunderWire.Editors;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UHFPS.Editors
{
    public class FoldableList : ReorderableList
    {
        private GUIContent headerTitle;
        private bool expanded;

        private SerializedProperty elements;
        private SerializedObject boundObject;
        private string elementsPath;

        private float HeaderHeight => base.headerHeight;
        private float FooterHeight => base.footerHeight;

        private bool IsExpanded
        {
            get
            {
                if (TryGetElements(out var prop))
                {
                    try { return prop.isExpanded; }
                    catch { }
                }
                return expanded;
            }
            set
            {
                if (TryGetElements(out var prop))
                {
                    try
                    {
                        prop.isExpanded = value;
                        return;
                    }
                    catch { }
                }
                expanded = value;
            }
        }

        private GUIStyle miniLabelFoldout
        {
            get
            {
                GUIStyle style = new(EditorStyles.foldout);
                style.font = EditorStyles.miniLabel.font;
                style.fontStyle = EditorStyles.miniLabel.fontStyle;
                style.fontSize = EditorStyles.miniLabel.fontSize;
                return style;
            }
        }

        public Action<Rect> OnHeaderDrawn;
        public Action<Rect> OnDrawFooter;
        public Action<Rect, int, bool, bool> OnDrawElement;
        public Func<int, float> OnElementHeight;

        private static MethodInfo mi_DoListHeader;
        private static MethodInfo mi_DoListElements;
        private static MethodInfo mi_DoListFooter;
        private static MethodInfo mi_GetListElementHeight;

        private static class FDefaults
        {
            public static Rect infinityRect = new Rect(0, 0, 10000, 10000);
        }

        public FoldableList(IList elements, Type elementType) : base(elements, elementType) { }

        public FoldableList(SerializedObject serializedObject, SerializedProperty elements) : base(serializedObject, elements)
        {
            BindProperty(elements);
        }

        public FoldableList(IList elements, Type elementType, bool draggable, bool displayHeader, bool displayAddButton, bool displayRemoveButton)
            : base(elements, elementType, draggable, displayHeader, displayAddButton, displayRemoveButton) { }

        public FoldableList(SerializedObject serializedObject, SerializedProperty elements, bool draggable, bool displayHeader, bool displayAddButton, bool displayRemoveButton)
            : base(serializedObject, elements, draggable, displayHeader, displayAddButton, displayRemoveButton)
        {
            BindProperty(elements);
        }

        /// <summary> Bind a live property (call this every draw from your drawer). </summary>
        public void BindProperty(SerializedProperty property)
        {
            if (property == null)
            {
                elements = null;
                boundObject = null;
                elementsPath = null;
                return;
            }

            boundObject = property.serializedObject;
            elementsPath = property.propertyPath;
            elements = property;
        }

        /// <summary> Try to get a live SerializedProperty. Re-resolves if needed. </summary>
        private bool TryGetElements(out SerializedProperty prop)
        {
            prop = null;

            if (boundObject == null || string.IsNullOrEmpty(elementsPath))
                return false;

            // If we still have a live prop referencing the same serializedObject, use it
            if (elements != null && elements.serializedObject == boundObject)
            {
                prop = elements;
                return true;
            }

            // Otherwise, try re-find it by path
            try
            {
                prop = boundObject.FindProperty(elementsPath);
                elements = prop;
                return prop != null;
            }
            catch
            {
                elements = null;
                return false;
            }
        }

        private void DoListHeader(Rect rect)
        {
            try
            {
                if (mi_DoListHeader == null)
                    mi_DoListHeader = typeof(ReorderableList).GetMethod("DoListHeader", BindingFlags.Instance | BindingFlags.NonPublic);

                mi_DoListHeader.Invoke(this, new object[] { rect });
            }
            catch { }
        }

        private void DoListElements(Rect rect, Rect visibleRect)
        {
            try
            {
                if (mi_DoListElements == null)
                    mi_DoListElements = typeof(ReorderableList).GetMethod("DoListElements", BindingFlags.Instance | BindingFlags.NonPublic);

                mi_DoListElements.Invoke(this, new object[] { rect, visibleRect });
            }
            catch { }
        }

        private void DoListFooter(Rect rect)
        {
            try
            {
                if (mi_DoListFooter == null)
                    mi_DoListFooter = typeof(ReorderableList).GetMethod("DoListFooter", BindingFlags.Instance | BindingFlags.NonPublic);

                mi_DoListFooter.Invoke(this, new object[] { rect });
            }
            catch { }
        }

        private float GetListElementHeight()
        {
            float height = 0f;

            try
            {
                if (mi_GetListElementHeight == null)
                    mi_GetListElementHeight = typeof(ReorderableList).GetMethod("GetListElementHeight", BindingFlags.Instance | BindingFlags.NonPublic);

                height = (float)mi_GetListElementHeight.Invoke(this, null);
            }
            catch { }
            return height;
        }

        /// <summary>
        /// Setup the foldable list with a header title and default callbacks.
        /// </summary>
        public void SetupFoldableList(GUIContent title)
        {
            headerTitle = title;
            drawHeaderCallback = DrawHeaderCallback;
            elementHeightCallback = OnElementHeightCallback;
            drawElementCallback = DoDrawElementCallback;        
        }

        /// <summary>
        /// Updates the title of the foldable list.
        /// </summary>
        public void UpdateTitle(GUIContent title)
        {
            headerTitle = title;
        }

        /// <summary>
        /// Draws the foldable list with a header and footer.
        /// </summary>
        public void DoLayoutListFoldable()
        {
            int indentLevel = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            GUILayout.BeginVertical();

            if (IsExpanded)
            {
                Rect rect = GUILayoutUtility.GetRect(0f, HeaderHeight, GUILayout.ExpandWidth(true));
                Rect rect2 = GUILayoutUtility.GetRect(10f, GetListElementHeight(), GUILayout.ExpandWidth(true));
                Rect rect3 = GUILayoutUtility.GetRect(4f, FooterHeight, GUILayout.ExpandWidth(true));

                DoListHeader(rect);
                if (boundObject != null)
                {
                    if (TryGetElements(out _))
                    {
                        DoListElements(rect2, FDefaults.infinityRect);
                        DoListFooter(rect3);
                    }
                }
                else
                {
                    DoListElements(rect2, FDefaults.infinityRect);
                    DoListFooter(rect3);
                }
            }
            else
            {
                Rect rect = GUILayoutUtility.GetRect(0f, HeaderHeight, GUILayout.ExpandWidth(true));
                DoListHeader(rect);
            }

            GUILayout.EndVertical();
            EditorGUI.indentLevel = indentLevel;
        }

        /// <summary>
        /// Draws the foldable list with a header and footer.
        /// </summary>
        /// <param name="rect"></param>
        public void DoListFoldable(Rect rect)
        {
            DoListFoldable(rect, FDefaults.infinityRect);
        }

        /// <summary>
        /// Draws the foldable list with a header and footer.
        /// </summary>
        public void DoListFoldable(Rect rect, Rect visibleRect)
        {
            int indentLevel = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            Rect headerRect = new(rect.x, rect.y, rect.width, HeaderHeight);
            Rect listRect = new(rect.x, headerRect.y + headerRect.height, rect.width, GetListElementHeight());
            Rect footerRect = new(rect.x, listRect.y + listRect.height, rect.width, FooterHeight);

            visibleRect.y -= headerRect.height;
            visibleRect.height -= headerRect.height;

            if (IsExpanded)
            {
                DoListHeader(headerRect);
                if (TryGetElements(out _))
                {
                    DoListElements(listRect, visibleRect);
                    DoListFooter(footerRect);
                }
            }
            else
            {
                DoListHeader(headerRect);
            }

            EditorGUI.indentLevel = indentLevel;
        }

        public float GetFoldableHeight()
        {
            if (!IsExpanded || !TryGetElements(out _))
                return HeaderHeight;

            // Safe path to base height
            try { return base.GetHeight(); }
            catch { return HeaderHeight; }
        }

        public virtual void DrawHeaderCallback(Rect rect)
        {
            if (!EditorDrawing.IsInEditorWindow)
            {
                const float leftMargin = 10f;
                rect.x += leftMargin;
                rect.xMax -= leftMargin;
            }

            OnHeaderDrawn?.Invoke(rect);

            bool newValue = EditorGUI.Foldout(rect, IsExpanded, headerTitle, true, miniLabelFoldout);
            if (newValue == IsExpanded)
                return;

            IsExpanded = newValue;
            ClearListCache();
        }

        private float OnElementHeightCallback(int index)
        {
            if (IsExpanded)
                return ElementHeightCallback(index);

            return 0f;
        }

        public virtual float ElementHeightCallback(int index)
        {
            if (OnElementHeight != null)
                return OnElementHeight(index);

            return EditorGUIUtility.singleLineHeight;
        }

        private void DoDrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (!IsExpanded)
                return;

            DrawElementCallback(rect, index, isActive, isFocused);
        }

        public virtual void DrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (OnDrawElement != null)
            {
                OnDrawElement(rect, index, isActive, isFocused);
                return;
            }

            if (elements == null)
                return;

            SerializedProperty element = elements.GetArrayElementAtIndex(index);
            defaultBehaviours.DrawElement(rect, element, null, isActive, isFocused, true);
        }

        public void ResetCache()
        {
            ClearListCache();
            CacheListIfNeeded();
        }

        private void ClearListCache()
        {
            var clearCacheMethod = typeof(ReorderableList).GetMethod("ClearCache", BindingFlags.Instance | BindingFlags.NonPublic)
                                    ?? typeof(ReorderableList).GetMethod("InvalidateCache", BindingFlags.Instance | BindingFlags.NonPublic);

            clearCacheMethod.Invoke(this, null);
        }

        private void CacheListIfNeeded()
        {
            var cacheIfNeededMethod = typeof(ReorderableList).GetMethod("CacheIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            cacheIfNeededMethod.Invoke(this, null);
        }
    }
}
