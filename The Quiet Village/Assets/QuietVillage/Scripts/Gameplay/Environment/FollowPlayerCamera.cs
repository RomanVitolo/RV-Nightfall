using QuietVillage.Multiplayer.Bridge;
using UnityEngine;

namespace QuietVillage.Gameplay.Environment
{
    /// <summary>
    /// Keeps a weather effect over this client's camera, so rain falls wherever the player is without covering the level.
    /// </summary>
    /// <remarks>
    /// Local presentation only; nothing is replicated. Every client's copy of the level has its own emitter, and each one
    /// follows the camera its client renders through, the player's or a spectator's alike, since both are the scene's
    /// main camera. Particles simulate in world space, so drops already falling stay where they are as the emitter moves.
    /// </remarks>
    public class FollowPlayerCamera : MonoBehaviour
    {
        [Tooltip("Where the emitter sits relative to the camera, in metres.")]
        [SerializeField] private Vector3 m_offset = new(0f, 14f, 0f);

        private ParticleSystem[] m_systems;

        private void Awake()
        {
            m_systems = GetComponentsInChildren<ParticleSystem>(true);

            foreach (var system in m_systems)
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            }
        }

        private void LateUpdate()
        {
            var camera = LocalPlayerContext.PlayerCamera;
            if (camera == null) camera = Camera.main;
            if (camera == null) return;

            transform.position = camera.transform.position + m_offset;
        }
    }
}
