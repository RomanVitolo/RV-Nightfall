using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UHFPS.Rendering
{
    [Serializable]
    public class BloodDisortionFeature : EffectFeature
    {
        public override string Name => "Blood Disortion";

        public RenderPassEvent RenderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        public Material EffectMaterial;

        public override void OnCreate()
        {
            RenderPass = new BloodDisortionRGPass(RenderPassEvent, EffectMaterial);
        }
    }
}