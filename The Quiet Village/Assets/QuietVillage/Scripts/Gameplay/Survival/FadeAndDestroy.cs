using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>Fades a light out over a few seconds, then removes its object.</summary>
    public class FadeAndDestroy : MonoBehaviour
    {
        private Light m_light;
        private float m_start;
        private float m_duration;
        private float m_intensity;

        public void Begin(Light light, float seconds)
        {
            m_light = light;
            m_intensity = light != null ? light.intensity : 0f;
            m_start = Time.time;
            m_duration = Mathf.Max(0.01f, seconds);
        }

        private void Update()
        {
            var t = (Time.time - m_start) / m_duration;
            if (m_light != null) m_light.intensity = m_intensity * (1f - Mathf.Clamp01(t));
            if (t >= 1f) Destroy(gameObject);
        }
    }
}
