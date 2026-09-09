using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UHFPS.Rendering
{
    [Serializable]
    public class EyeBlinkFeature : EffectFeature
    {
        public override string Name => "Eye Blink";

        public RenderPassEvent RenderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        public Material EffectMaterial;

        public override void OnCreate()
        {
            RenderPass = new EyeBlinkRGPass(RenderPassEvent, EffectMaterial);
        }
    }
}