using UHFPS.Runtime;
using UnityEditor;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Finds the player's managers while the Editor is not playing.
    /// </summary>
    /// <remarks>
    /// UHFPS's inspector drawers and the inventory builder used to reach these through
    /// <c>Inventory.Instance</c> and <c>PlayerPresenceManager.Instance</c>. Those singletons are gone,
    /// and <see cref="LocalPlayerContext"/> cannot stand in for them here — it is populated by the
    /// owning client when a player spawns, so in edit mode it is always empty.
    ///
    /// The managers now live on the player prefab, so the prefab asset is the authority. The open
    /// scene is checked first, which covers editing inside Prefab Stage or having a player instance
    /// placed for testing.
    /// </remarks>
    public static class PlayerAssetLookup
    {
        /// <summary>
        /// The networked player prefab. The only path to it in the codebase — HeroPlayerSetup builds this
        /// same asset, so the two can never disagree about where the player lives.
        /// </summary>
        internal const string PlayerPrefabPath = "Assets/Modules/Multiplayer/Prefabs/NetworkedWorkerPlayer.prefab";

        /// <summary>The player's inventory, or <c>null</c> if the prefab cannot be found.</summary>
        public static Inventory FindInventory() => FindOnPlayer<Inventory>();

        /// <summary>The player root, or <c>null</c> if the prefab cannot be found.</summary>
        public static GameObject FindPlayerRoot()
        {
            var presence = FindOnPlayer<PlayerPresenceManager>();
            if (presence != null) return presence.gameObject;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            return prefab != null ? prefab : null;
        }

        private static T FindOnPlayer<T>() where T : Component
        {
            var inScene = Object.FindFirstObjectByType<T>(FindObjectsInactive.Include);
            if (inScene != null) return inScene;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            return prefab != null ? prefab.GetComponentInChildren<T>(true) : null;
        }
    }
}
