using System.Collections;
using QuietVillage.Multiplayer.Bridge.World;
using QuietVillage.Multiplayer.Characters;
using Newtonsoft.Json.Linq;
using QuietVillage.Multiplayer.Player;
using QuietVillage.Multiplayer.Sessions;
using Unity.Cinemachine;
using Unity.Collections;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Components;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
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

        // Owner-written: a name is the player's own to declare, and nothing but other players' UI reads it.
        private readonly NetworkVariable<FixedString128Bytes> m_displayName =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>This player's room name, readable on every client; "Player N" until the owner publishes it.</summary>
        public string DisplayName
        {
            get
            {
                var displayName = m_displayName.Value.ToString();
                return string.IsNullOrEmpty(displayName) ? $"Player {OwnerClientId + 1}" : displayName;
            }
        }

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

            // What this player had when the game was saved, if the room is resuming one.
            var world = FindAnyObjectByType<WorldSync>();
            var savedPlayer = world != null ? world.TakeLocalPlayerSave() : null;

            if (savedPlayer == null || !MoveToSavedPlace(savedPlayer)) MoveToSpawnPoint();

            BindPlayerCamera();
            BindScenePostProcessing();
            PublishDisplayName();
            AdaptMenusToSession();
            ShareDroppedItems();
            HandleBeingDowned();

            // A player returning mid-level starts empty-handed: what they had was dropped where they died.
            var character = GetComponent<PlayerCharacter>();
            var returning = character != null && character.Returning;
            ApplyRolePerks(freshStart: savedPlayer == null && !returning);

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

            // Their belongings, once the managers holding them have started: an inventory restored before then is
            // overwritten by the items a new game begins with.
            if (savedPlayer != null) StartCoroutine(RestoreSavedPlayer(savedPlayer));
        }

        /// <summary>Puts this player back where the save left them, instead of on a spawn point.</summary>
        /// <returns><c>false</c> if the save has no place for them, e.g. they are new to it.</returns>
        private bool MoveToSavedPlace(JToken savedPlayer)
        {
            if (!Saves.PlayerSaveState.TryGetTransform(savedPlayer, out var position, out _)) return false;

            // As MoveToSpawnPoint does: the controller writes the transform back every Move().
            var characterController = GetComponent<CharacterController>();
            var wasEnabled = characterController != null && characterController.enabled;
            if (wasEnabled) characterController.enabled = false;

            transform.position = position;

            if (wasEnabled) characterController.enabled = true;

            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null && networkTransform.CanCommitToTransform)
                networkTransform.Teleport(transform.position, transform.rotation, transform.localScale);

            return true;
        }

        private IEnumerator RestoreSavedPlayer(JToken savedPlayer)
        {
            // End of frame: every Start has run by now, including the inventory's.
            yield return null;

            Saves.PlayerSaveState.Apply(gameObject, savedPlayer);

            // The look direction is part of the save, and the presence manager owns it.
            if (Saves.PlayerSaveState.TryGetTransform(savedPlayer, out var position, out var rotation))
                LocalPlayerContext.Presence.SetPlayerTransform(position, rotation);
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

            // Through the GameManager rather than its fields: it also re-reads the blur radius and rain overlay, which
            // its Awake could not find before these existed.
            gameManager.BindPostProcessing(sceneReferences.GlobalPostProcessing, sceneReferences.HealthPostProcessing);
        }

        /// <summary>
        /// Replaces the pause and death menu actions that would take this player out of the session, and turns the
        /// death screen into spectating.
        /// </summary>
        /// <remarks>
        /// Added at runtime rather than on the prefab: both only ever belong on the owner's copy, and they find
        /// their buttons themselves. Must run before the HUD's first Start; see <see cref="SessionMenus.Bind"/>.
        /// </remarks>
        private void AdaptMenusToSession()
        {
            var gameManager = GetComponentInChildren<GameManager>(true);
            if (gameManager == null) return;

            var menus = GetComponent<SessionMenus>();
            if (menus == null) menus = gameObject.AddComponent<SessionMenus>();

            menus.Bind(gameManager, IsServer);

            // After the menus: it takes over the death screen's Restart, which SessionMenus has hidden.
            var health = GetComponent<PlayerHealthSync>();
            if (health == null) return;

            var deathScreen = GetComponent<SessionDeathScreen>();
            if (deathScreen == null) deathScreen = gameObject.AddComponent<SessionDeathScreen>();

            deathScreen.Bind(gameManager, health, IsServer);
        }

        /// <summary>
        /// Makes what this player drops from the inventory appear for everyone, not only here, and keeps what they
        /// carry in play when they die or leave.
        /// </summary>
        private void ShareDroppedItems()
        {
            var inventory = GetComponentInChildren<Inventory>(true);
            if (inventory == null) return;

            inventory.DropSync = new DroppedItems();

            var health = GetComponent<PlayerHealthSync>();
            if (health == null) return;

            var carried = GetComponent<CarriedItems>();
            if (carried == null) carried = gameObject.AddComponent<CarriedItems>();

            carried.Bind(inventory, health);
        }

        /// <summary>Makes being down crawl, disarm and count down for the player at this machine.</summary>
        private void HandleBeingDowned()
        {
            var health = GetComponent<PlayerHealthSync>();
            if (health == null) return;

            var downed = GetComponent<DownedPlayer>();
            if (downed == null) downed = gameObject.AddComponent<DownedPlayer>();

            downed.Bind(health);
        }

        /// <summary>Gives this player their role's run speed, and on a fresh start its extra slots and starting items.</summary>
        private void ApplyRolePerks(bool freshStart)
        {
            var character = GetComponent<PlayerCharacter>();
            if (character == null) return;

            var perks = GetComponent<LocalRolePerks>();
            if (perks == null) perks = gameObject.AddComponent<LocalRolePerks>();

            perks.Bind(character, freshStart);
        }

        /// <summary>
        /// Dresses this copy in its player's chosen character, before anything else takes hold of the body.
        /// </summary>
        /// <remarks>
        /// First in <see cref="ConfigureAsRemoteAvatar"/>: the held item, the name tag and the viewmodel sweep all take
        /// the body's Animator, and would keep the destroyed one if this ran after them. The owner keeps the authored body,
        /// which nobody ever sees.
        /// </remarks>
        private void ApplyCharacterBody()
        {
            var character = GetComponent<PlayerCharacter>();
            if (m_avatar == null || character == null || character.Character == null) return;

            var catalog = character.Catalog;
            if (!m_avatar.ReplaceBody(character.Character, character.Choice.Variant,
                    catalog != null ? catalog.BodyAnimator : null))
            {
                Debug.LogWarning($"{nameof(HeroPlayerNetworkSetup)}: could not build '{character.Choice}' for " +
                                 $"{DisplayName}; they appear as the default body.", this);
            }
        }

        /// <summary>Publishes the name this player chose in the lobby, for the others' UI.</summary>
        private void PublishDisplayName()
        {
            // Lives on the persistent multiplayer root.
            var sessions = FindAnyObjectByType<SessionService>();
            if (sessions == null) return;

            foreach (var member in sessions.Members)
            {
                if (!member.IsLocal) continue;

                var displayName = new FixedString128Bytes();
                displayName.CopyFromTruncated(member.DisplayName);
                m_displayName.Value = displayName;
                return;
            }
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
            ApplyCharacterBody();
            DisablePlayerManagers();
            DisableFirstPersonStack();
            DisableViewmodelAnimators();
            DisableLocalOnlyOutputs();
            GiveAvatarSoundAndLight();

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

            // Not a PlayerComponent, so the loop above misses it. Its Update drives the HUD, the blood volume and
            // fall damage, all of which belong to the owner; this body's health arrives through PlayerHealthSync.
            var health = GetComponent<PlayerHealth>();
            if (health != null) health.enabled = false;

            // CharacterController's overlap recovery keeps depenetrating the capsule away from the
            // positions NetworkTransform writes, which reads as jitter. Remote players become
            // non-solid as a result — you can walk through them until they get a collider of their own.
            var characterController = GetComponent<CharacterController>();
            if (characterController != null) characterController.enabled = false;
        }

        /// <summary>
        /// Lets this body be heard and seen: its footsteps and item sounds, and the light it carries.
        /// </summary>
        /// <remarks>
        /// After the outputs above are switched off, so the AudioSource it adds for the body is not caught by that
        /// sweep. Added at runtime rather than on the prefab: neither belongs on the copy its owner plays.
        /// </remarks>
        private void GiveAvatarSoundAndLight()
        {
            var locomotion = GetComponent<PlayerLocomotionSync>();
            var actions = GetComponent<PlayerActionSync>();
            var footsteps = GetComponentInChildren<FootstepsSystem>(true);

            if (locomotion != null && actions != null && footsteps != null)
                gameObject.AddComponent<AvatarSounds>().Bind(locomotion, actions, footsteps);

            if (locomotion != null && actions != null)
                gameObject.AddComponent<AvatarHeldLight>().Bind(actions, locomotion.LookTransform);

            // The body's own animator, and any inventory for the item database every copy shares.
            var avatarAnimator = m_avatar != null ? m_avatar.Animator : null;
            var inventory = GetComponentInChildren<Inventory>(true);

            if (actions != null && avatarAnimator != null)
                gameObject.AddComponent<AvatarHeldItem>().Bind(actions, avatarAnimator, inventory);

            ShowNameTag(avatarAnimator);

            // Held Use on this body revives it while its player is down.
            var health = GetComponent<PlayerHealthSync>();
            if (health != null) ReviveTarget.Attach(health, this);
        }

        /// <summary>Hangs this player's name over their body for whoever is looking.</summary>
        private void ShowNameTag(Animator avatarAnimator)
        {
            var health = GetComponent<PlayerHealthSync>();
            if (health == null) return;

            // The HUD's own font, so a name looks like the rest of the game rather than like default TMP.
            var hudText = GetComponentInChildren<TMP_Text>(true);
            var font = hudText != null ? hudText.font : TMP_Settings.defaultFontAsset;
            if (font == null)
            {
                Debug.LogWarning($"{nameof(HeroPlayerNetworkSetup)}: no TextMeshPro font found, " +
                                 "so player names will not be shown.", this);
                return;
            }

            gameObject.AddComponent<AvatarNameTag>().Bind(this, health, avatarAnimator, font);
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
