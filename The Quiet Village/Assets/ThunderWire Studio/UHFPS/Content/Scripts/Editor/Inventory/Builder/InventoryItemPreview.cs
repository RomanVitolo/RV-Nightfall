using UnityEditor.Sprites;
using UnityEditor;
using UnityEngine;
using UHFPS.Runtime;
using ThunderWire.Editors;

namespace UHFPS.Editors
{
    public static class InventoryItemPreview
    {
        private static Material _previewMat;
        private static Material PreviewMat
        {
            get
            {
                if (_previewMat == null)
                {
                    var sh = Shader.Find("Hidden/Internal-GUITextureClip");
                    if (sh == null) sh = Shader.Find("Unlit/Transparent");
                    _previewMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _previewMat;
            }
        }

        public static void ShowItemSlotPreview(PropertyCollection props, Rect previewRect, int slotSize)
        {
            int slotWidth = Mathf.Max(1, props["Width"].intValue);
            int slotHeight = Mathf.Max(1, props["Height"].intValue);
            float padding = props["Padding"].floatValue;

            ImageOrientation orientation = (ImageOrientation)props["Orientation"].enumValueIndex;
            FlipDirection flipDirection = (FlipDirection)props["FlipDirection"].enumValueIndex;
            Sprite icon = props["Icon"].objectReferenceValue as Sprite;

            if (icon == null)
            {
                slotWidth = 1;
                slotHeight = 1;
            }

            EditorGUI.DrawRect(previewRect, new Color(0.13f, 0.13f, 0.13f, 1f));
            GUI.Box(previewRect, GUIContent.none, EditorStyles.helpBox);

            var inner = Shrink(previewRect, 6f);
            ComputeGrid(inner, slotWidth, slotHeight, out Rect gridRect, out float cell);

            if (icon != null)
            {
                DrawSlotGrid(gridRect, slotWidth, slotHeight, new Color(1f, 1f, 1f, 0.95f), new Color(1f, 1f, 1f, 0.25f));
                Vector2 slotSizeGame = new(slotWidth * slotSize, slotHeight * slotSize);

                const float kSlotMargin = 20f;
                slotSizeGame -= new Vector2(kSlotMargin, kSlotMargin);
                slotSizeGame -= new Vector2(padding, padding);

                slotSizeGame.x = Mathf.Max(0.001f, slotSizeGame.x);
                slotSizeGame.y = Mathf.Max(0.001f, slotSizeGame.y);

                Vector2 iconSize = icon.rect.size;
                Vector2 newIconSize = orientation == ImageOrientation.Normal
                    ? iconSize : new Vector2(iconSize.y, iconSize.x);

                Vector2 scaleRatio = slotSizeGame / newIconSize;
                float scaleFactor = Mathf.Min(scaleRatio.x, scaleRatio.y);

                Vector2 iconDrawSizeGame = iconSize * scaleFactor;
                float previewPixelsPerGamePixel = cell / slotSize;
                Vector2 drawSizePreview = iconDrawSizeGame * previewPixelsPerGamePixel;

                // icon flipping
                float angle = 0f;
                if (orientation == ImageOrientation.Flipped)
                    angle = flipDirection == FlipDirection.Left ? -90f : 90f;

                // draw the icon
                Rect drawRect = CenterRectWithSize(gridRect, drawSizePreview);
                DrawSprite(icon, drawRect, angle);
            }
            else
            {
                // draw a placeholder label when no icon is assigned
                Vector2 placeholderSize = new Vector2(cell * slotWidth, cell * slotHeight) * 0.5f;
                Rect placeholderRect = CenterRectWithSize(gridRect, placeholderSize);
                placeholderRect.xMin = gridRect.xMin + 2f;
                placeholderRect.xMax = gridRect.xMax - 2f;

                GUIContent noIconText = new("No Icon");
                GUIStyle noIconStyle = new(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic
                };

                EditorGUI.LabelField(placeholderRect, noIconText, noIconStyle);
            }
        }

        private static void ComputeGrid(Rect host, int w, int h, out Rect gridRect, out float cellSize)
        {
            float cellW = host.width / w;
            float cellH = host.height / h;
            cellSize = Mathf.Min(cellW, cellH);

            // CENTER the slot within host
            Vector2 gridSize = new(cellSize * w, cellSize * h);
            float x = host.center.x - gridSize.x * 0.5f;
            float y = host.center.y - gridSize.y * 0.5f;
            gridRect = new Rect(x, y, gridSize.x, gridSize.y);
        }

        private static Rect CenterRectWithSize(Rect container, Vector2 size)
        {
            float x = container.center.x - size.x * 0.5f;
            float y = container.center.y - size.y * 0.5f;
            return new Rect(x, y, size.x, size.y);
        }

        private static void DrawSlotGrid(Rect rect, int w, int h, Color frame, Color grid)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            Handles.BeginGUI();
            {
                // Outer frame
                Handles.color = frame;
                Handles.DrawLine(new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin));
                Handles.DrawLine(new Vector2(rect.xMax, rect.yMin), new Vector2(rect.xMax, rect.yMax));
                Handles.DrawLine(new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax));
                Handles.DrawLine(new Vector2(rect.xMin, rect.yMax), new Vector2(rect.xMin, rect.yMin));

                // Inner grid
                Handles.color = grid;

                if (w > 1)
                {
                    float dx = rect.width / w;
                    for (int i = 1; i < w; i++)
                    {
                        float x = rect.x + i * dx;
                        Handles.DrawLine(new Vector2(x, rect.yMin), new Vector2(x, rect.yMax));
                    }
                }

                if (h > 1)
                {
                    float dy = rect.height / h;
                    for (int j = 1; j < h; j++)
                    {
                        float y = rect.y + j * dy;
                        Handles.DrawLine(new Vector2(rect.xMin, y), new Vector2(rect.xMax, y));
                    }
                }
            }
            Handles.EndGUI();
        }

        private static void DrawSprite(Sprite s, Rect target, float rotationDegrees)
        {
            if (s == null) return;

            Texture2D tex = SpriteUtility.GetSpriteTexture(s, false);
            if (tex == null) return;

            Matrix4x4 old = GUI.matrix;
            if (!Mathf.Approximately(rotationDegrees, 0f))
            {
                Vector2 pivot = target.center;
                GUIUtility.RotateAroundPivot(rotationDegrees, pivot);
            }

            EditorGUI.DrawPreviewTexture(target, tex, PreviewMat, ScaleMode.ScaleToFit, 0, 0);
            GUI.matrix = old;
        }

        private static Rect Shrink(Rect r, float pixels)
        {
            return new Rect(r.x + pixels, r.y + pixels, r.width - 2 * pixels, r.height - 2 * pixels);
        }
    }
}
