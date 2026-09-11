using Unity.Cinemachine;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// A camera over a teammate's shoulder, for a player who has died.
    /// </summary>
    /// <remarks>
    /// A CinemachineCamera of its own at a priority above the player's, so the scene's single brain blends to it
    /// and the dead player's camera is left alone. It follows the teammate's camera holder, whose rotation (yaw
    /// and pitch) already replicates, so the view turns wherever they look.
    ///
    /// Created in the level scene rather than under the player, so a level restart removes it with everything else.
    /// </remarks>
    public sealed class SpectatorCamera
    {
        private const int Priority = 100;

        private readonly GameObject m_root;
        private readonly CinemachineCamera m_camera;

        public SpectatorCamera()
        {
            m_root = new GameObject("SpectatorCamera");
            m_camera = m_root.AddComponent<CinemachineCamera>();
            m_camera.Priority = Priority;

            var follow = m_root.AddComponent<CinemachineThirdPersonFollow>();
            follow.ShoulderOffset = new Vector3(0.45f, 0.1f, 0f);
            follow.VerticalArmLength = 0.2f;
            follow.CameraSide = 1f;
            follow.CameraDistance = 2.2f;
            follow.Damping = new Vector3(0.1f, 0.25f, 0.3f);

            // Remote players have no active collider (their CharacterController is off), so walls are all it meets.
            var obstacles = follow.AvoidObstacles;
            obstacles.Enabled = true;
            obstacles.CollisionFilter = Physics.DefaultRaycastLayers;
            obstacles.IgnoreTag = "Player";
            obstacles.CameraRadius = 0.15f;
            obstacles.DampingIntoCollision = 0f;
            obstacles.DampingFromCollision = 0.5f;
            follow.AvoidObstacles = obstacles;
        }

        /// <summary>Cuts to a new teammate's camera holder.</summary>
        public void Follow(Transform lookTransform)
        {
            if (m_camera == null) return;

            m_camera.Follow = lookTransform;

            // A cut, not a sweep across the level through whatever lies between the two players.
            m_camera.PreviousStateIsValid = false;
        }

        /// <summary>Removes the camera; the brain goes back to the player's own.</summary>
        public void Destroy()
        {
            if (m_root != null) Object.Destroy(m_root);
        }
    }
}
