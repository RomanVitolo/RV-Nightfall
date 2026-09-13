using Newtonsoft.Json.Linq;
using QuietVillage.Multiplayer.Characters;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.Saves
{
    /// <summary>
    /// One player's half of a save: what they carry, what they have been asked to do, and where they stand.
    /// </summary>
    /// <remarks>
    /// Only the client that plays a player can answer for them. Their inventory, objectives and health live on
    /// their own machine, and on every other machine that copy of them is switched off and knows nothing. So each
    /// client captures itself when the host asks, and applies its own slice when the level reopens.
    ///
    /// The same three managers UHFPS saves, in the same formats, so a co-op save restores exactly what a single
    /// player save would.
    /// </remarks>
    public static class PlayerSaveState
    {
        /// <summary>Captures the local player. Call on the client that owns them.</summary>
        /// <returns>Their data, or <c>null</c> if the player is not fully spawned yet.</returns>
        public static StorableCollection Capture(GameObject player)
        {
            if (player == null) return null;

            var presence = player.GetComponentInChildren<PlayerPresenceManager>(true);
            var manager = player.GetComponent<PlayerManager>();
            var inventory = player.GetComponentInChildren<Inventory>(true);
            var objectives = player.GetComponentInChildren<ObjectiveManager>(true);

            if (presence == null || manager == null) return null;

            var (position, rotation) = presence.GetPlayerTransform();

            var state = new StorableCollection
            {
                { "position", position.ToSaveable() },
                { "rotation", rotation.ToSaveable() },
                { "localData", manager.OnCustomSave() }
            };

            if (inventory != null) state.Add("inventory", ((ISaveableCustom)inventory).OnCustomSave());
            if (objectives != null) state.Add("objectives", ((ISaveableCustom)objectives).OnCustomSave());

            // Who they were: resuming puts them back in the same role, whatever they have picked in the lobby since.
            var character = player.GetComponent<PlayerCharacter>();
            if (character != null && !character.Choice.IsEmpty) state.Add(CharacterKey, character.Choice.Encode());

            return state;
        }

        private const string CharacterKey = "character";

        /// <summary>The character a saved player was playing.</summary>
        /// <returns><c>false</c> if the save predates characters or holds none for them.</returns>
        public static bool TryGetCharacter(JToken state, out CharacterChoice choice)
        {
            choice = CharacterChoice.None;

            var saved = state?[CharacterKey];
            if (saved == null || saved.Type != JTokenType.String) return false;

            choice = CharacterChoice.Parse((string)saved);
            return !choice.IsEmpty;
        }

        /// <summary>Where a saved player was standing, for spawning them there instead of at a spawn point.</summary>
        /// <returns><c>true</c> if the save holds a position.</returns>
        public static bool TryGetTransform(JToken state, out Vector3 position, out Vector2 rotation)
        {
            position = Vector3.zero;
            rotation = Vector2.zero;

            if (state?["position"] == null || state["rotation"] == null) return false;

            position = state["position"].ToObject<Vector3>();
            rotation = state["rotation"].ToObject<Vector2>();
            return true;
        }

        /// <summary>
        /// Gives the local player back what they had. Call on the client that owns them, once their managers have
        /// started: an inventory restored before then is overwritten by the items a new game starts with.
        /// </summary>
        public static void Apply(GameObject player, JToken state)
        {
            if (player == null || state == null) return;

            var manager = player.GetComponent<PlayerManager>();
            var inventory = player.GetComponentInChildren<Inventory>(true);
            var objectives = player.GetComponentInChildren<ObjectiveManager>(true);

            if (inventory != null && state["inventory"] != null)
                ((ISaveableCustom)inventory).OnCustomLoad(state["inventory"]);

            if (objectives != null && state["objectives"] != null)
                ((ISaveableCustom)objectives).OnCustomLoad(state["objectives"]);

            // Last: it restores health and the item in their hands, which the two above can feed.
            if (manager != null && state["localData"] != null) manager.OnCustomLoad(state["localData"]);
        }
    }
}
