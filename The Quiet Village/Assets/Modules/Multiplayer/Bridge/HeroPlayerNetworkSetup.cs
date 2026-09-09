using Modules.Multiplayer.Scripts.Runtime.Player;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Splits a spawned HEROPLAYER between the client that owns it and the clients that only observe it.
    /// </summary>
    /// <remarks>
    /// Authority: owner. The owning client runs UHFPS's <see cref="PlayerStateMachine"/> and its
    /// <c>CharacterController.Move</c>, and the NetworkTransforms on this prefab replicate the result;
    /// a first-person camera cannot absorb the round trip of server-authoritative movement. Health is
    /// the deliberate exception and stays server-owned — see <see cref="PlayerHealthSync"/>.
    ///
    /// HEROPLAYER carries roughly 53 components, most of which assume they are the one and only local
    /// player: ten viewmodel Animators, twelve AudioSources, a HUD Canvas and a Cinemachine camera.
    /// Every one of those has to be shut off on remote copies, or four connected players will hear
    /// each other's footsteps at their own position and fight over the scene's single CinemachineBrain.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject))]
    public class HeroPlayerNetworkSetup : NetworkBehaviour
    {
        [Tooltip("Third-person stand-in shown to other clients.")]
        [SerializeField] private RemoteAvatarBinder m_avatar;

        public override void OnNetworkSpawn()
        {
            if (IsOwner) ConfigureAsLocalPlayer();
            else ConfigureAsRemoteAvatar();
        }

        public override void OnNetworkDespawn()
        {
            // A stale registration would point every world script at a destroyed player.
            if (IsOwner) LocalPlayerContext.Unbind();
        }

        private void ConfigureAsLocalPlayer()
        {
            // The owner is inside this body; drawing it would fill the screen with its own chest.
            if (m_avatar != null) m_avatar.SetAvatarVisible(false);

            MoveToSpawnPoint();
            BindPlayerCamera();
            BindScenePostProcessing();

            // UHFPS resolves the player through this manager from about ten different systems (AI
            // targeting, doors, dialogue, cutscenes). Its scene reference is empty until we hand it
            // the player Netcode just spawned.
            // Register with the one explicit global before anything reads it — UHFPS world code
            // resolves this client's managers through LocalPlayerContext from here on.
            LocalPlayerContext.Bind(gameObject);

            var presence = LocalPlayerContext.Presence;
            if (presence == null)
            {
                Debug.LogError(
                    $"{nameof(HeroPlayerNetworkSetup)}: the player prefab has no {nameof(PlayerPresenceManager)}; " +
                    "UHFPS cannot resolve the local player.", this);
                return;
            }

            presence.BindPlayer(gameObject);
        }

        /// <summary>
        /// Restores the GameManager's post-processing Volume references.
        /// </summary>
        /// <remarks>
        /// These are scene lighting, so they did not travel with the HUD onto the player prefab, and a
        /// prefab cannot hold a scene reference. Without this the health/damage vignette and the global
        /// grade are simply missing — no error, just no effect.
        /// </remarks>
        private void BindScenePostProcessing()
        {
            var gameManager = GetComponentInChildren<GameManager>(true);
            if (gameManager == null) return;

            var sceneReferences = SceneGameReferences.Active;
            if (sceneReferences == null)
            {
                Debug.LogWarning(
                    $"{nameof(HeroPlayerNetworkSetup)}: no {nameof(SceneGameReferences)} in the scene, " +
                    "so post-processing volumes are unassigned.", this);
                return;
            }

            if (gameManager.GlobalPPVolume == null)
                gameManager.GlobalPPVolume = sceneReferences.GlobalPostProcessing;

            if (gameManager.HealthPPVolume == null)
                gameManager.HealthPPVolume = sceneReferences.HealthPostProcessing;
        }

        /// <summary>
        /// Points UHFPS at the scene camera this client renders through.
        /// </summary>
        /// <remarks>
        /// <c>PlayerManager.MainCamera</c> is a *scene* reference, which a prefab cannot hold — it is
        /// null on every spawned player. UHFPS never assigns it at runtime because in single player it
        /// was wired in the Inspector on the scene instance. Left null, <c>LookController.Update</c>
        /// throws on <c>MainCamera.fieldOfView</c> every frame and aiming does not work at all.
        ///
        /// Resolved through the CinemachineBrain rather than <c>Camera.main</c>: the scene's camera is
        /// untagged, so <c>Camera.main</c> returns null here.
        /// </remarks>
        private void BindPlayerCamera()
        {
            var playerManager = GetComponent<PlayerManager>();
            if (playerManager == null)
            {
                Debug.LogError($"{nameof(HeroPlayerNetworkSetup)}: no {nameof(PlayerManager)} on the player.", this);
                return;
            }

            if (playerManager.MainCamera != null) return;

            var brain = FindFirstObjectByType<CinemachineBrain>(FindObjectsInactive.Exclude);
            var sceneCamera = brain != null ? brain.OutputCamera : Camera.main;

            if (sceneCamera == null)
            {
                Debug.LogError(
                    $"{nameof(HeroPlayerNetworkSetup)}: no CinemachineBrain or main camera in the scene, " +
                    "so UHFPS has no camera to aim with.", this);
                return;
            }

            playerManager.MainCamera = sceneCamera;
        }

        /// <summary>
        /// Places the owning player on its spawn point.
        /// </summary>
        /// <remarks>
        /// Authority: owner, matching the rest of this player's movement. Netcode instantiates the
        /// player prefab at the prefab's own transform — the origin — and GameplayScene has no floor
        /// there, so without this the player falls out of the level immediately.
        /// </remarks>
        private void MoveToSpawnPoint()
        {
            var spawnPoints = PlayerSpawnPoints.Active;
            if (spawnPoints == null ||
                !spawnPoints.TryGetSpawnPoint(OwnerClientId, out var position, out var rotation))
            {
                Debug.LogError(
                    $"{nameof(HeroPlayerNetworkSetup)}: no usable {nameof(PlayerSpawnPoints)} in the scene. " +
                    "The player will spawn at the world origin and fall out of the level.", this);
                return;
            }

            // CharacterController writes the transform back every Move(), so a plain assignment is
            // undone on the next frame. It has to be off while the player is repositioned.
            var characterController = GetComponent<CharacterController>();
            var controllerWasEnabled = characterController != null && characterController.enabled;
            if (controllerWasEnabled) characterController.enabled = false;

            transform.SetPositionAndRotation(position, rotation);

            if (controllerWasEnabled) characterController.enabled = true;

            // Tell NetworkTransform this was a jump rather than motion, so observers do not watch the
            // player interpolate across the level from the origin.
            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null && networkTransform.CanCommitToTransform)
            {
                networkTransform.Teleport(transform.position, transform.rotation, transform.localScale);
            }
        }

        private void ConfigureAsRemoteAvatar()
        {
            DisablePlayerManagers();
            DisableFirstPersonStack();
            DisableViewmodelAnimators();
            DisableLocalOnlyOutputs();

            // Only now is this object safe to draw for everyone else.
            if (m_avatar != null) m_avatar.SetAvatarVisible(true);
        }

        /// <summary>
        /// Shuts down the per-player managers on a body this client does not own.
        /// </summary>
        /// <remarks>
        /// These moved onto the player prefab when they stopped being singletons, so every client now
        /// instantiates four of each. Only the owner's copy may run — a remote one would drive this
        /// client's HUD, pause state and inventory. They are not PlayerComponents, so the blanket
        /// disable below does not reach them.
        /// </remarks>
        private void DisablePlayerManagers()
        {
            DisableManager<GameManager>();
            DisableManager<Inventory>();
            DisableManager<PlayerPresenceManager>();

            // These moved onto the prefab with the HUD, because they hold references into it.
            DisableManager<DialogueSystem>();
            DisableManager<ObjectiveManager>();
            DisableManager<JumpscareManager>();
            DisableManager<SaveGameManager>();
            DisableManager<OptionsManager>();
        }

        private void DisableManager<T>() where T : Behaviour
        {
            var manager = GetComponentInChildren<T>(true);
            if (manager != null) manager.enabled = false;
        }

        /// <summary>Stops UHFPS from simulating a player this client does not control.</summary>
        private void DisableFirstPersonStack()
        {
            // PlayerComponent is UHFPS's base for every player subsystem — state machine, look,
            // examine, footsteps, item controllers. Disabling by base type keeps this correct as
            // UHFPS adds subsystems, instead of naming each one and silently missing new ones.
            foreach (var component in GetComponentsInChildren<PlayerComponent>(true))
            {
                if (component == null) continue;

                component.SetEnabled(false);
                component.enabled = false;
            }

            // CharacterController's overlap recovery keeps depenetrating the capsule away from the
            // positions NetworkTransform writes, which reads as jitter. Remote players become
            // non-solid as a result — you can walk through them until they get a collider of their own.
            var characterController = GetComponent<CharacterController>();
            if (characterController != null) characterController.enabled = false;
        }

        /// <summary>Silences the first-person arm, candle and item Animators on remote copies.</summary>
        private void DisableViewmodelAnimators()
        {
            var avatarAnimator = m_avatar != null ? m_avatar.Animator : null;

            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null) continue;

                // The avatar's animator is the one thing on a remote copy that must keep running.
                if (avatarAnimator != null && animator == avatarAnimator) continue;

                animator.enabled = false;
            }
        }

        /// <summary>
        /// Disables outputs that only make sense for the player sitting at this machine.
        /// </summary>
        private void DisableLocalOnlyOutputs()
        {
            foreach (var audioSource in GetComponentsInChildren<AudioSource>(true))
            {
                if (audioSource != null) audioSource.enabled = false;
            }

            // More than one listener makes Unity warn and picks an arbitrary winner.
            foreach (var listener in GetComponentsInChildren<AudioListener>(true))
            {
                if (listener != null) listener.enabled = false;
            }

            // Another player's HUD must never render into this client's view.
            foreach (var canvas in GetComponentsInChildren<Canvas>(true))
            {
                if (canvas != null) canvas.enabled = false;
            }

            // The scene has a single CinemachineBrain. Four live virtual cameras would compete for it
            // and the view would snap between players.
            foreach (var virtualCamera in GetComponentsInChildren<CinemachineCamera>(true))
            {
                if (virtualCamera != null) virtualCamera.enabled = false;
            }

            foreach (var playerCamera in GetComponentsInChildren<Camera>(true))
            {
                if (playerCamera != null) playerCamera.enabled = false;
            }
        }
    }
}
