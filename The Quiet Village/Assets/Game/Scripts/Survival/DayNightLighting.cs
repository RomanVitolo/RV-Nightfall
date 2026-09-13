using UnityEngine;
using UnityEngine.Rendering;

namespace QuietVillage.Survival
{
    /// <summary>
    /// Turns the level's clock into light: a sun that crosses the sky, a red dusk, and a dark, foggy night.
    /// </summary>
    /// <remarks>
    /// Runs on every client from the replicated clock, so everyone sees the same sky without the light itself being
    /// sent. Pure presentation: nothing in the game reads the light, so a client that misjudges server time by a
    /// frame only shades a frame differently.
    ///
    /// Fog carries most of the night. It hides how far away something is, which is what the creatures need, and it
    /// works the same in a greybox as in a finished level.
    /// </remarks>
    [RequireComponent(typeof(SurvivalDirector))]
    public class DayNightLighting : MonoBehaviour
    {
        [Tooltip("Directional light used as sun by day and moon by night.")]
        [SerializeField] private Light m_sun;

        [Header("Day")]
        [SerializeField] private Color m_dayColor = new(1f, 0.95f, 0.86f);
        [SerializeField] private float m_dayIntensity = 1.1f;
        [SerializeField] private Color m_dayAmbient = new(0.42f, 0.44f, 0.48f);
        [SerializeField] private Color m_dayFog = new(0.62f, 0.66f, 0.7f);
        [SerializeField] private float m_dayFogDensity = 0.004f;

        [Header("Dusk")]
        [Tooltip("Share of the day, at its end, spent turning to dusk.")]
        [Range(0f, 0.5f)] [SerializeField] private float m_duskShare = 0.2f;
        [SerializeField] private Color m_duskColor = new(1f, 0.45f, 0.25f);
        [SerializeField] private float m_duskIntensity = 0.4f;
        [SerializeField] private Color m_duskAmbient = new(0.22f, 0.16f, 0.16f);
        [SerializeField] private Color m_duskFog = new(0.36f, 0.24f, 0.22f);

        [Header("Night")]
        [SerializeField] private Color m_nightColor = new(0.45f, 0.55f, 0.8f);
        [SerializeField] private float m_nightIntensity = 0.05f;
        [SerializeField] private Color m_nightAmbient = new(0.03f, 0.035f, 0.05f);
        [SerializeField] private Color m_nightFog = new(0.02f, 0.025f, 0.035f);
        [SerializeField] private float m_nightFogDensity = 0.06f;

        [Tooltip("Seconds over which night deepens after dusk, and dawn brightens after the night.")]
        [SerializeField] private float m_blendSeconds = 12f;

        private SurvivalDirector m_director;
        private SurvivalPhase m_lastPhase;
        private float m_phaseSeenAt;

        private void Awake()
        {
            m_director = GetComponent<SurvivalDirector>();
        }

        private void OnEnable()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.ambientMode = AmbientMode.Flat;
        }

        private void Update()
        {
            var phase = m_director != null && m_director.IsSpawned ? m_director.Phase : SurvivalPhase.Day;
            if (phase != m_lastPhase)
            {
                m_lastPhase = phase;
                m_phaseSeenAt = Time.time;
            }

            var sinceChange = Time.time - m_phaseSeenAt;

            switch (phase)
            {
                case SurvivalPhase.Day:
                    ApplyDay();
                    break;

                case SurvivalPhase.Night:
                    Apply(Dusk(), Night(), Blend(sinceChange), 40f);
                    break;

                default:
                    Apply(Night(), Day(), Blend(sinceChange), Mathf.Lerp(40f, 20f, Blend(sinceChange)));
                    break;
            }
        }

        private void ApplyDay()
        {
            var duration = m_director != null ? m_director.PhaseDuration : 0f;
            var progress = duration > 0f ? 1f - m_director.SecondsRemaining / duration : 0f;

            // The sun rises from the east, crosses, and sets as the dusk share begins.
            var angle = Mathf.Lerp(25f, 170f, progress);
            var duskStart = 1f - m_duskShare;
            var dusk = m_duskShare > 0f ? Mathf.InverseLerp(duskStart, 1f, progress) : 0f;

            Apply(Day(), Dusk(), dusk, angle);
        }

        private float Blend(float seconds) => m_blendSeconds > 0f ? Mathf.Clamp01(seconds / m_blendSeconds) : 1f;

        private void Apply(Look from, Look to, float t, float sunAngle)
        {
            if (m_sun != null)
            {
                m_sun.color = Color.Lerp(from.Light, to.Light, t);
                m_sun.intensity = Mathf.Lerp(from.Intensity, to.Intensity, t);
                m_sun.transform.rotation = Quaternion.Euler(sunAngle, -30f, 0f);
            }

            RenderSettings.ambientLight = Color.Lerp(from.Ambient, to.Ambient, t);
            RenderSettings.fogColor = Color.Lerp(from.Fog, to.Fog, t);
            RenderSettings.fogDensity = Mathf.Lerp(from.FogDensity, to.FogDensity, t);
        }

        private Look Day() => new(m_dayColor, m_dayIntensity, m_dayAmbient, m_dayFog, m_dayFogDensity);

        private Look Dusk() => new(m_duskColor, m_duskIntensity, m_duskAmbient, m_duskFog,
            Mathf.Lerp(m_dayFogDensity, m_nightFogDensity, 0.3f));

        private Look Night() => new(m_nightColor, m_nightIntensity, m_nightAmbient, m_nightFog, m_nightFogDensity);

        private readonly struct Look
        {
            public readonly Color Light;
            public readonly float Intensity;
            public readonly Color Ambient;
            public readonly Color Fog;
            public readonly float FogDensity;

            public Look(Color light, float intensity, Color ambient, Color fog, float fogDensity)
            {
                Light = light;
                Intensity = intensity;
                Ambient = ambient;
                Fog = fog;
                FogDensity = fogDensity;
            }
        }
    }
}
