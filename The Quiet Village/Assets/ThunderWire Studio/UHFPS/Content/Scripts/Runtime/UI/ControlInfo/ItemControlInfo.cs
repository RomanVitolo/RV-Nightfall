using System;
using UnityEngine;
using UnityEngine.UI;
using UHFPS.Scriptable;
using UHFPS.Input;
using ThunderWire.Attributes;
using TMPro;

namespace UHFPS.Runtime
{
    [InspectorHeader("Item Control Info")]
    public class ItemControlInfo : MonoBehaviour
    {
        public TMP_Text ActionText;
        public Image GlyphSprite;
        public Vector2 GlyphSize;
        public bool ScaleWithGlyphScale = false;
        
        private IDisposable disposable;

        private void OnDestroy()
        {
            disposable.Dispose();
        }

        public void SetControlInfo(ControlInfo info)
        {
            ActionText.text = info.Text;

            var bindingPath = InputManager.GetBindingPath(info.Input.ActionName, info.Input.BindingIndex);
            disposable = bindingPath.InputGlyphObservable.Subscribe(OnBindingChange);
        }

        private void OnBindingChange(InputGlyph glyph)
        {
            GlyphSprite.sprite = glyph.GlyphSprite;

            if (ScaleWithGlyphScale && GlyphSprite.TryGetComponent(out LayoutElement layout))
            {
                // Scale the glyph size based on the specified scale in the InputSpritesAsset.
                Vector2 newSize = Vector2.Scale(GlyphSize, glyph.GlyphScale);
                layout.preferredWidth = newSize.x;
                layout.preferredHeight = newSize.y;
            }
        }
    }
}
