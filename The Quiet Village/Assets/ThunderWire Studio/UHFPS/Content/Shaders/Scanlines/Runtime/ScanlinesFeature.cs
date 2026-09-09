using UnityEngine.Rendering.Universal;
using UnityEngine;
using System;

namespace UHFPS.Rendering
{
    [Serializable]
    public class ScanlinesFeature : EffectFeature
    {
        public override string Name => "Scanlines";

        public RenderPassEvent RenderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        public Material EffectMaterial;

        public override void OnCreate()
        {
            RenderPass = new ScanlinesRGPass(RenderPassEvent, EffectMaterial);
        }

    }
}