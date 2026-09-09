using UnityEngine;
using ThunderWire.Attributes;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Flame Flicker")]
    public class FlameFlicker : MonoBehaviour
    {
        public Light FlameLight;
        public MinMax FlameFlickerLimits;
        public float FlameFlickerSpeed = 1f;

        [Header("Position Flicker")]
        public bool PositionFlicker;
        public float PositionFlickerSpeed = 1f;
        public Vector3 PositionFlickerMagnitude = new(0.1f, 0.1f, 0.1f);

        [Header("Optimization")]
        public bool Optimize = true;
        public float FlickerDistance = 10f;

        private Transform player;
        private Vector3 originalPosition;

        private void Awake()
        {
            originalPosition = transform.position;
        }

        private void Update()
        {
            if (!FlameLight.enabled)
                return;

            // MULTIPLAYER PATCH: the player is spawned by Netcode after scene load, so it cannot be
            // cached in Awake any more. Resolve on first use instead.
            if (player == null)
            {
                var localPlayer = LocalPlayerContext.Player;
                if (localPlayer != null) player = localPlayer.transform;
            }

            // Optimization (skipped until a player exists to measure distance against)
            if (Optimize && player != null && Vector3.Distance(transform.position, player.position) > FlickerDistance)
                return;

            // Intensity Flicker
            float flicker = Mathf.PerlinNoise1D(Time.time * FlameFlickerSpeed);
            FlameLight.intensity = Mathf.Lerp(FlameFlickerLimits.RealMin, FlameFlickerLimits.RealMax, flicker);

            // Position Flicker
            if (PositionFlicker)
            {
                float xOffset = Perlin(Time.time * PositionFlickerSpeed, 1f);
                float yOffset = Perlin(Time.time * PositionFlickerSpeed, 2f);
                float zOffset = Perlin(Time.time * PositionFlickerSpeed, 3f);

                Vector3 flickerPosition = new(xOffset, yOffset, zOffset);
                transform.position = originalPosition + Vector3.Scale(flickerPosition, PositionFlickerMagnitude);
            }
        }

        private float Perlin(float x, float y)
        {
            float value = Mathf.PerlinNoise(x, y);
            return (value - 0.5f) * 2;
        }
    }
}