using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UHFPS.Rendering
{
    [Serializable]
    public class RaindropFeature : EffectFeature
    {
        public override string Name => "Raindrop";

        public RenderPassEvent RenderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        public Material EffectMaterial;

        public override void OnCreate()
        {
            RenderPass = new RaindropRGPass(RenderPassEvent, EffectMaterial);
        }
    }
}