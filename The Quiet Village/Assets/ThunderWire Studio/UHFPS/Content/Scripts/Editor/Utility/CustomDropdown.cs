using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using UnityEditor;
using UnityEngine;

namespace UHFPS.Editors
{
    public enum EDropdownAnchor
    {
        /// <summary>
        /// Draw dropdown aligned to the left of the target rect and expand to the right.
        /// </summary>
        Left,

        /// <summary>
        /// Draw dropdown aligned to the middle of the target rect.
        /// </summary>
        Middle,

        /// <summary>
        /// Draw dropdown aligned to the right of the target rect and expand to the left.
        /// </summary>
        Right
    }

    public struct CustomDropdownItem
    {
        public bool IsSelected { get; set; }
        public bool IsSeparator { get; set; }
        public readonly string ItemOrEmpty => Item == null ? string.Empty : Item.ToString();

        public object Item;
        public string Path;
        public string Icon;

        public static CustomDropdownItem Separator
        {
            get => new CustomDropdownItem()
            {
                IsSelected = false,
                IsSeparator = true,
                Path = "Separator",
                Item = null,
                Icon = ""
            };
        }

        public CustomDropdownItem(string path, bool separator)
        {
            IsSelected = false;
            IsSeparator = separator;
            Path = path + "/Separator";
            Item = null;
            Icon = "";
        }

        public CustomDropdownItem(string path)
        {
            IsSelected = false;
            IsSeparator = false;
            Path = path;
            Item = path;
            Icon = "";
        }

        public CustomDropdownItem(string path, object item)
        {
            IsSelected = false;
            IsSeparator = false;
            Path = path;
            Item = item;
            Icon = "";
        }

        public CustomDropdownItem(string path, object item, string icon)
        {
            IsSelected = false;
            IsSeparator = false;
            Path = path;
            Item = item;
            Icon = icon;
        }
    }

    public struct CustomDropdownData
    {
        public string Name;
        public bool InsertNone;
        public IEnumerable<CustomDropdownItem> Items;

        public Rect ContentRect;
        public EDropdownAnchor Anchor;

        public float Width;
        public float Height;
    }

    public class CustomDropdown : AdvancedDropdown
    {
        public const float DROPDOWN_WIDTH = 250f;
        public const float DROPDOWN_HEIGHT = 270f;
        public const string NONE_ITEM_NAME = "<none>";

        private readonly IEnumerable<CustomDropdownItem> items;
        private readonly string dropdownName;

        public Action<CustomDropdownItem> OnItemSelected;
        public string NoneItemName = NONE_ITEM_NAME;

        private class DropdownItem : AdvancedDropdownItem
        {
            public CustomDropdownItem item;

            public DropdownItem(string displayName, CustomDropdownItem item) : base(displayName)
            {
                this.item = item;
            }
        }

        public CustomDropdown(AdvancedDropdownState state, string dropdownName, IEnumerable<CustomDropdownItem> items, bool insertNone = false) : base(state)
        {
            if (insertNone == true)
            {
                CustomDropdownItem nullItem = new(NoneItemName, null);
                CustomDropdownItem[] nullArr = new[] { nullItem };
                this.items = nullArr.Concat(items);
            }
            else this.items = items;

            this.dropdownName = dropdownName;
            minimumSize = new Vector2(minimumSize.x, DROPDOWN_HEIGHT);
        }

        public static void DropdownButton(string propertyLabel, string buttonLabel, CustomDropdownData dropdownData, Action<CustomDropdownItem> OnItemSelected)
        {
            Rect rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(propertyLabel));
            DropdownButton(rect, buttonLabel, dropdownData, OnItemSelected);
        }

        public static void DropdownButton(Rect rect, string buttonLabel, CustomDropdownData dropdownData, Action<CustomDropdownItem> OnItemSelected)
        {
            dropdownData.ContentRect = rect;
            if (GUI.Button(rect, new GUIContent(buttonLabel), EditorStyles.popup))
            {
                CreateAndShow(dropdownData, OnItemSelected);
            }
        }

        public static CustomDropdown CreateAndShow(CustomDropdownData dropdownData, Action<CustomDropdownItem> OnItemSelected)
        {
            CustomDropdown dropdown = new(new AdvancedDropdownState(), dropdownData.Name, dropdownData.Items, dropdownData.InsertNone);
            dropdown.OnItemSelected = OnItemSelected;
            dropdown.Show(dropdownData.ContentRect, dropdownData.Width, dropdownData.Height, dropdownData.Anchor);
            return dropdown;
        }

        public void Show(Rect contentRect, EDropdownAnchor anchor)
        {
            float width = Mathf.Max(DROPDOWN_WIDTH, minimumSize.x);
            float height = Mathf.Max(DROPDOWN_HEIGHT, minimumSize.y);
            Show(contentRect, width, height, anchor);
        }

        public void Show(Rect contentRect, float width, EDropdownAnchor anchor)
        {
            width = Mathf.Max(DROPDOWN_WIDTH, width);
            float height = Mathf.Max(DROPDOWN_HEIGHT, minimumSize.y);
            Show(contentRect, width, height, EDropdownAnchor.Left);
        }

        public void Show(Rect contentRect, float width, float height, EDropdownAnchor anchor)
        {
            var _width = width > 0f ? width : DROPDOWN_WIDTH;
            var _height = height > 0f ? height : DROPDOWN_HEIGHT;
            minimumSize = new Vector2(_width, _height);

            var _anchor = new Rect(contentRect)
            {
                y = contentRect.yMax,
                height = 1f
            };

            switch (anchor)
            {
                // Draw dropdown aligned to the left of the target rect and expand to the right.
                case EDropdownAnchor.Left:
                    _anchor.x = contentRect.xMin;
                    _anchor.width = _width;
                    break;

                // Draw dropdown aligned to the middle of the target rect.
                case EDropdownAnchor.Middle:
                    _anchor.x = contentRect.xMin + (contentRect.width - _width) * 0.5f;
                    _anchor.width = _width;
                    break;

                // Draw dropdown aligned to the right of the target rect and expand to the left.
                case EDropdownAnchor.Right:
                    _anchor.x = contentRect.xMax - _width;
                    _anchor.width = _width;
                    break;

                default:
                    // Sensible fallback
                    _anchor.x = contentRect.xMin;
                    _anchor.width = _width;
                    break;
            }

            _anchor.x = Mathf.Round(_anchor.x);
            _anchor.width = Mathf.Round(_anchor.width);

            base.Show(_anchor);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(dropdownName);
            var groupMap = new Dictionary<string, AdvancedDropdownItem>();

            foreach (var item in items)
            {
                // split the name into groups
                string path = item.Path;
                string[] groups = path.Split('/');

                // create or find the groups
                AdvancedDropdownItem parent = root;
                for (int i = 0; i < groups.Length - 1; i++)
                {
                    string groupPath = string.Join("/", groups.Take(i + 1));
                    if (!groupMap.ContainsKey(groupPath))
                    {
                        var newGroup = new AdvancedDropdownItem(groups[i]);
                        parent.AddChild(newGroup);
                        groupMap[groupPath] = newGroup;
                    }
                    parent = groupMap[groupPath];
                }

                if (item.IsSeparator)
                {
                    parent.AddSeparator();
                }
                else
                {
                    // create the item and add it to the last group
                    DropdownItem dropItem = new(groups.Last(), item);

                    if (item.IsSelected)
                    {
                        Texture icon = EditorGUIUtility.IconContent("Checkmark").image;
                        dropItem.icon = icon as Texture2D;
                    }
                    else if (!string.IsNullOrEmpty(item.Icon))
                    {
                        Texture icon = EditorGUIUtility.TrIconContent(item.Icon).image;
                        dropItem.icon = icon as Texture2D;
                    }

                    parent.AddChild(dropItem);
                }
            }

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            DropdownItem element = (DropdownItem)item;
            OnItemSelected?.Invoke(element.item);
        }
    }
}