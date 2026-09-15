using UnityEngine;
using UnityEngine.AI;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// A lamp on the generator's circuit, and the patch of ground it keeps creatures off at night.
    /// </summary>
    /// <remarks>
    /// Its lights only show what the director decided (<see cref="SurvivalDirector"/>); it holds no state. The protected
    /// ground is a circle on the ground plane rather than the light's actual cone, so what creatures avoid is predictable
    /// and the same on every machine, whatever the light's angle or shadows.
    /// </remarks>
    public class Floodlight : MonoBehaviour
    {
        [Tooltip("Lights switched with the generator.")]
        [SerializeField] private Light[] m_lights = System.Array.Empty<Light>();

        [Tooltip("Renderers whose emission turns on with the lights, e.g. the lamp's glass.")]
        [SerializeField] private Renderer[] m_glow = System.Array.Empty<Renderer>();

        [Tooltip("Where the lit ground is centred, usually out in front of the lamp.")]
        [SerializeField] private Transform m_centre;

        [Tooltip("Radius (m) of ground creatures will not stand on while the light is on.")]
        [SerializeField] private float m_radius = 7f;

        private const float EdgeMargin = 1.5f;
        private const string EmissionKeyword = "_EMISSION";

        public Vector3 Centre => m_centre != null ? m_centre.position : transform.position;

        public float Radius => m_radius;

        private void Awake() => SetLit(false);

        public bool Covers(Vector3 position)
        {
            var offset = position - Centre;
            offset.y = 0f;
            return offset.sqrMagnitude <= m_radius * m_radius;
        }

        /// <summary>The nearest walkable point just outside this light, from a position inside it.</summary>
        public Vector3 PointOutside(Vector3 position)
        {
            var away = position - Centre;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;

            var target = Centre + away.normalized * (m_radius + EdgeMargin);
            target.y = position.y;

            return NavMesh.SamplePosition(target, out var hit, 3f, NavMesh.AllAreas) ? hit.position : target;
        }

        public void SetLit(bool lit)
        {
            foreach (var light in m_lights)
            {
                if (light != null) light.enabled = lit;
            }

            foreach (var glow in m_glow)
            {
                if (glow == null) continue;

                // Instance materials: switching emission on the shared one would light every lamp using it, lit or not.
                foreach (var material in glow.materials)
                {
                    if (lit) material.EnableKeyword(EmissionKeyword);
                    else material.DisableKeyword(EmissionKeyword);
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.9f, 0.5f, 0.6f);
            var centre = Centre;
            const int segments = 32;
            for (var i = 0; i < segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                var b = (i + 1) * Mathf.PI * 2f / segments;
                Gizmos.DrawLine(centre + new Vector3(Mathf.Cos(a), 0.1f, Mathf.Sin(a)) * m_radius,
                    centre + new Vector3(Mathf.Cos(b), 0.1f, Mathf.Sin(b)) * m_radius);
            }
        }

#if UNITY_EDITOR
        public void Configure(Light[] lights, Renderer[] glow, Transform centre, float radius)
        {
            m_lights = lights;
            m_glow = glow;
            m_centre = centre;
            m_radius = radius;
        }
#endif
    }
}
