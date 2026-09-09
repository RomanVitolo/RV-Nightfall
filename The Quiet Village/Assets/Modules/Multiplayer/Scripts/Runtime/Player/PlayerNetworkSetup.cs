using Modules.Multiplayer.Scripts.Runtime.Cameras;
using Starter_Assets.Runtime.InputSystem;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using StarterAssetsFirstPersonController =
    Starter_Assets.Runtime.FirstPersonController.Scripts.FirstPersonController;

namespace Modules.Multiplayer.Scripts.Runtime.Player
{
    /// <summary>
    /// Splits a spawned player between the client that owns it and the clients that merely observe it.
    /// </summary>
    /// <remarks>
    /// Authority: owner. The owning client simulates its own movement and look, and the two
    /// NetworkTransforms on this prefab replicate the result — a first-person camera cannot absorb the
    /// round-trip latency of server-authoritative movement. The trade-off is that a modified client can
    /// place itself anywhere, which is acceptable for co-op and would not be for competitive play.
    /// Spawning itself stays server-authoritative: NGO instantiates NetworkManager.PlayerPrefab and
    /// assigns ownership to the connecting client.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject))]
    public class PlayerNetworkSetup : NetworkBehaviour
    {
        [Tooltip("The PlayerCameraRoot child. Pitch lives here, and the local camera binds to it.")]
        [SerializeField] private Transform m_cameraRoot;

        public override void OnNetworkSpawn()
        {
            if (IsOwner) EnableLocalControl();
            else DisableRemoteAvatar();
        }

        public override void OnNetworkDespawn()
        {
            if (!IsOwner) return;

            var rig = PlayerCameraRig.Active;
            if (rig != null) rig.Unbind();
        }

        private void EnableLocalControl()
        {
            if (m_cameraRoot == null)
            {
                Debug.LogError(
                    $"{nameof(PlayerNetworkSetup)}: Camera Root is unassigned, so the local camera cannot follow this player.",
                    this);
                return;
            }

            var rig = PlayerCameraRig.Active;
            if (rig == null)
            {
                Debug.LogError(
                    $"{nameof(PlayerNetworkSetup)}: no {nameof(PlayerCameraRig)} in the scene, so the local player has no camera.",
                    this);
                return;
            }

            rig.BindTo(m_cameraRoot);
        }

        private void DisableRemoteAvatar()
        {
            // Every player object in this process runs its own input stack. Left enabled, local input
            // would drive the remote avatars too and every capsule would move together.
            var controller = GetComponent<StarterAssetsFirstPersonController>();
            if (controller != null) controller.enabled = false;

            var inputs = GetComponent<StarterAssetsInputs>();
            if (inputs != null) inputs.enabled = false;

            var playerInput = GetComponent<PlayerInput>();
            if (playerInput != null) playerInput.enabled = false;

            // CharacterController's overlap recovery keeps depenetrating the capsule away from the
            // positions NetworkTransform writes, which shows up as jitter on remote avatars. Disabling
            // it makes them display-only, so players pass through each other until we give remotes a
            // plain collider of their own.
            var characterController = GetComponent<CharacterController>();
            if (characterController != null) characterController.enabled = false;
        }
    }
}
