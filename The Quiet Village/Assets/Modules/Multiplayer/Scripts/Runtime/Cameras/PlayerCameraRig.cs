using Unity.Cinemachine;
using UnityEngine;

namespace Modules.Multiplayer.Scripts.Runtime.Cameras
{
    /// <summary>
    /// The single Cinemachine camera belonging to this client, re-pointed at whichever player object
    /// the local client owns.
    /// </summary>
    /// <remarks>
    /// There is one rig per <em>client</em>, not per player: a second live CinemachineCamera would
    /// compete with this one for the scene's single CinemachineBrain and the view would flip between
    /// players. The camera therefore lives in the scene and the spawning player binds to it, rather
    /// than each player prefab carrying its own camera.
    /// </remarks>
    [RequireComponent(typeof(CinemachineCamera))]
    public class PlayerCameraRig : MonoBehaviour
    {
        /// <summary>
        /// The rig in the loaded scene, or <c>null</c> if none is present.
        /// </summary>
        /// <remarks>
        /// A static stands in for the DI container we are deferring — a network-spawned player has no
        /// other handle on a scene service. Replace with an injected reference once DI lands.
        /// </remarks>
        public static PlayerCameraRig Active { get; private set; }

        private CinemachineCamera m_camera;

        private void Awake()
        {
            m_camera = GetComponent<CinemachineCamera>();
            if (m_camera == null)
            {
                Debug.LogError($"{nameof(PlayerCameraRig)} needs a {nameof(CinemachineCamera)} on the same GameObject.", this);
                return;
            }

            Active = this;
        }

        private void OnDestroy()
        {
            // Guard against clobbering a rig from a scene that loaded before this one unloaded.
            if (ReferenceEquals(Active, this)) Active = null;
        }

        /// <summary>Points the camera at the local player's head transform.</summary>
        /// <param name="target">The owning player's camera root. Ignored when null.</param>
        public void BindTo(Transform target)
        {
            if (m_camera == null || target == null)
            {
                Debug.LogWarning($"{nameof(PlayerCameraRig)}.{nameof(BindTo)} ignored a null camera or target.", this);
                return;
            }

            m_camera.Follow = target;

            // Without this the camera blends in from wherever it was last frame — usually the world
            // origin — which reads as the player falling into place on spawn.
            m_camera.PreviousStateIsValid = false;
        }

        /// <summary>Releases the camera when the local player despawns.</summary>
        public void Unbind()
        {
            if (m_camera == null) return;

            m_camera.Follow = null;
        }
    }
}
