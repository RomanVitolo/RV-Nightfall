using System;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// The one place world code asks "which player belongs to this client?".
    /// </summary>
    /// <remarks>
    /// UHFPS resolved <c>GameManager</c>, <c>Inventory</c> and <c>PlayerPresenceManager</c> through
    /// three separate <c>Singleton&lt;T&gt;</c> statics, each of which assumed a single scene-placed
    /// player. Those are now per-player MonoBehaviours on the player prefab.
    ///
    /// Roughly forty world-side scripts — doors, triggers, puzzles, HUD widgets — have no hierarchy
    /// path to a player, so a process-level lookup is unavoidable. This replaces three implicit
    /// globals with one explicit one, written only by the owning client when its player spawns.
    ///
    /// Every accessor returns null rather than throwing: the window between scene load and the local
    /// player spawning is real, and callers that run during that window must be able to see "not yet"
    /// instead of taking an exception.
    /// </remarks>
    public static class LocalPlayerContext
    {
        private static GameObject s_player;
        private static GameManager s_gameManager;
        private static Inventory s_inventory;
        private static PlayerPresenceManager s_presence;
        private static PlayerManager s_playerManager;
        private static JumpscareManager s_jumpscareManager;

        /// <summary>Raised when a local player becomes available, for code that started earlier.</summary>
        public static event Action Ready;

        /// <summary>True once this client owns a spawned player.</summary>
        public static bool IsReady => s_player != null;

        /// <summary>This client's player object, or <c>null</c> before it spawns.</summary>
        public static GameObject Player => s_player != null ? s_player : null;

        public static GameManager GameManager => s_gameManager != null ? s_gameManager : null;

        public static Inventory Inventory => s_inventory != null ? s_inventory : null;

        public static PlayerPresenceManager Presence => s_presence != null ? s_presence : null;

        public static PlayerManager PlayerManager => s_playerManager != null ? s_playerManager : null;

        /// <summary>Plays jumpscares on this client's screen, or <c>null</c> before the player spawns.</summary>
        public static JumpscareManager JumpscareManager => s_jumpscareManager != null ? s_jumpscareManager : null;

        /// <summary>Whether a collider or component belongs to this client's player, rather than a teammate's body.</summary>
        /// <remarks>
        /// For world triggers whose effect is this client's alone, such as a jumpscare. A remote copy's collider is
        /// normally off, but a teammate's body entering a trigger must never play its effect on this screen.
        /// </remarks>
        public static bool IsLocalPlayer(Component component) =>
            component != null && s_player != null && component.transform.IsChildOf(s_player.transform);

        /// <summary>This client's camera, or <c>null</c> before the player spawns.</summary>
        public static Camera PlayerCamera
        {
            get
            {
                var playerManager = PlayerManager;
                return playerManager != null ? playerManager.MainCamera : null;
            }
        }

        /// <summary>This client's state machine, or <c>null</c> before the player spawns.</summary>
        public static PlayerStateMachine StateMachine
        {
            get
            {
                var playerManager = PlayerManager;
                return playerManager != null ? playerManager.PlayerStateMachine : null;
            }
        }

        /// <summary>
        /// Resolves the <see cref="GameManager"/> for a component that lives on a player.
        /// </summary>
        /// <remarks>
        /// Scripts on the player run their <c>Awake</c> during <c>Instantiate</c>, which is *before*
        /// <c>OnNetworkSpawn</c> registers this client's player — so the registry is still empty then
        /// and caching from it in Awake yields null. Their own hierarchy already has the answer, and it
        /// is the right answer even on a remote copy, where the registry would hand back this client's
        /// player instead of the one they belong to.
        /// </remarks>
        /// <param name="context">The component asking. Falls back to the registry when null.</param>
        public static GameManager ResolveGameManager(Component context) => Resolve(context, GameManager);

        /// <summary>Resolves the <see cref="Inventory"/> for a component that lives on a player.</summary>
        /// <remarks>See <see cref="ResolveGameManager"/>.</remarks>
        public static Inventory ResolveInventory(Component context) => Resolve(context, Inventory);

        /// <summary>Resolves the <see cref="PlayerPresenceManager"/> for a component on a player.</summary>
        /// <remarks>See <see cref="ResolveGameManager"/>.</remarks>
        public static PlayerPresenceManager ResolvePresence(Component context) => Resolve(context, Presence);

        private static T Resolve<T>(Component context, T registryValue) where T : Component
        {
            if (context != null)
            {
                var owned = context.GetComponentInParent<T>(true);
                if (owned != null) return owned;
            }

            return registryValue;
        }

        /// <summary>
        /// Registers the player this client owns. Called once, by the owning client only.
        /// </summary>
        /// <param name="localPlayer">The spawned player object. Ignored when null.</param>
        public static void Bind(GameObject localPlayer)
        {
            if (localPlayer == null)
            {
                Debug.LogError($"{nameof(LocalPlayerContext)}.{nameof(Bind)} was given a null player.");
                return;
            }

            s_player = localPlayer;

            // Managers now live on the player prefab, so they come from the object itself. Searching
            // children too keeps this working if they are later nested under the player rather than
            // sitting on its root.
            s_gameManager = localPlayer.GetComponentInChildren<GameManager>(true);
            s_inventory = localPlayer.GetComponentInChildren<Inventory>(true);
            s_presence = localPlayer.GetComponentInChildren<PlayerPresenceManager>(true);
            s_playerManager = localPlayer.GetComponentInChildren<PlayerManager>(true);
            s_jumpscareManager = localPlayer.GetComponentInChildren<JumpscareManager>(true);

            WarnIfMissing(s_gameManager, nameof(GameManager));
            WarnIfMissing(s_inventory, nameof(Inventory));
            WarnIfMissing(s_presence, nameof(PlayerPresenceManager));
            WarnIfMissing(s_playerManager, nameof(PlayerManager));

            Ready?.Invoke();
        }

        /// <summary>Clears the registration when the local player despawns.</summary>
        public static void Unbind()
        {
            s_player = null;
            s_gameManager = null;
            s_inventory = null;
            s_presence = null;
            s_playerManager = null;
            s_jumpscareManager = null;
        }

        private static void WarnIfMissing(Component component, string name)
        {
            if (component != null) return;

            Debug.LogError(
                $"{nameof(LocalPlayerContext)}: the local player has no {name}. " +
                "Re-run Tools > Multiplayer > Set Up Networked HEROPLAYER to put the managers on the prefab.");
        }
    }
}
