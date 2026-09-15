using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>A burning flare on the ground: a red stick, a flickering red light and its crackle, gone when it burns out.</summary>
    /// <remarks>
    /// Presentation only, built on every client from the director's replicated flare list. Creatures keep out of its
    /// light because the director says so (<see cref="SurvivalDirector.IsLit"/>), not because of this light.
    /// </remarks>
    public class FlareVisual : MonoBehaviour
    {
        private static readonly Color FlareRed = new(1f, 0.25f, 0.12f);

        private Light m_light;
        private float m_endsAt;
        private float m_baseIntensity;
        private float m_seed;

        public static GameObject Create(Vector3 position, float radius, float seconds)
        {
            var root = new GameObject("Flare");
            root.transform.position = position;

            SurvivalProps.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0.12f, 0f), new Vector3(0.05f, 0.12f, 0.05f),
                FlareRed, Quaternion.Euler(70f, 0f, 0f), emission: 4f);

            var lightHolder = new GameObject("Light");
            lightHolder.transform.SetParent(root.transform, false);
            lightHolder.transform.localPosition = new Vector3(0f, 0.6f, 0f);

            var flare = root.AddComponent<FlareVisual>();
            flare.m_light = lightHolder.AddComponent<Light>();
            flare.m_light.type = LightType.Point;
            flare.m_light.color = FlareRed;
            flare.m_light.range = radius * 1.8f;
            flare.m_baseIntensity = 4f;
            flare.m_light.intensity = flare.m_baseIntensity;
            flare.m_light.shadows = LightShadows.None;
            flare.m_endsAt = Time.time + seconds;
            flare.m_seed = Random.value * 100f;

            var audio = root.AddComponent<AudioSource>();
            audio.clip = SynthSounds.Crackle;
            audio.loop = true;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Logarithmic;
            audio.minDistance = 1.5f;
            audio.maxDistance = 20f;
            audio.volume = 0.5f;
            audio.Play();

            return root;
        }

        private void Update()
        {
            var remaining = m_endsAt - Time.time;
            if (remaining <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            // A flare sputters, and dies down over its last seconds.
            var flicker = Mathf.Lerp(0.75f, 1.15f, Mathf.PerlinNoise(m_seed, Time.time * 9f));
            m_light.intensity = m_baseIntensity * flicker * Mathf.Clamp01(remaining / 2f);
        }
    }
}
