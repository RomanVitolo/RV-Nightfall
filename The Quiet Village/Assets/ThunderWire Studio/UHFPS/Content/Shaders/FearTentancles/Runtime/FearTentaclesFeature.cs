using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UHFPS.Rendering
{
    [Serializable]
    public class FearTentaclesFeature : EffectFeature
    {
        public override string Name => "Fear Tentacles";

        public RenderPassEvent RenderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        public Material EffectMaterial;

        public override void OnCreate()
        {
            RenderPass = new FearTentaclesRGPass(RenderPassEvent, EffectMaterial);
        }
    }
}