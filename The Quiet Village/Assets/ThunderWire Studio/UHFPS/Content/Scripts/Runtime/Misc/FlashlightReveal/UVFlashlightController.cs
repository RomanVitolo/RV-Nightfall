using UnityEngine;
using ThunderWire.Attributes;

namespace UHFPS.Runtime
{
    [InspectorHeader("UV Flashlight Reveal")]
    public class UVFlashlightController : MonoBehaviour
    {
        public Light Flashlight;
        public bool LightEnabled;

        public float FlashlightIntensity { get; set; } = 1f;
        
        private void Awake()
        {
            if (Flashlight == null)
                Flashlight = GetComponent<Light>();
        }

        private void LateUpdate()
        {
            if (Flashlight == null)
                return;

            // If the light is disabled, clear the reveal data.
            if (!Flashlight.enabled || !LightEnabled)
            {
                foreach (var reveal in UVFlashlightReveal.Reveals)
                {
                    if (reveal == null)
                        continue;
                    
                    reveal.ClearLightData();
                }
                
                return;
            }

            Vector3 lightPosition = Flashlight.transform.position;
            Vector3 lightDirection = Flashlight.transform.forward;
            float lightAngle = Flashlight.spotAngle * 0.5f * Mathf.Deg2Rad;
            float lightRange = Flashlight.range;

            // Push light data to all registered reveals
            foreach (var reveal in UVFlashlightReveal.Reveals)
            {
                if (reveal == null)
                    continue;
                
                reveal.SetLightData(lightPosition, lightDirection, lightAngle, lightRange, FlashlightIntensity);
            }
        }
        
        /// <summary>
        /// Enable/Disable the UV Flashlight reveal effect.
        /// </summary>
        public void EnableLight (bool enable)
        {
            UVFlashlightReveal.IsUVFlashlightEnabled = true;
            LightEnabled = enable;
        }
    }
}