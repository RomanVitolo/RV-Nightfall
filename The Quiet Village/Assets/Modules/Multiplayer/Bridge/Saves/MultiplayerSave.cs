using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.Saves
{
    /// <summary>
    /// What a co-op save holds: the world, each player's own belongings, and the items left lying about.
    /// </summary>
    /// <remarks>
    /// Players are keyed by their Unity account, not by the order they joined, so resuming a save weeks later
    /// hands each person back their own inventory whoever else is in the room. A player with no entry, joining a
    /// save for the first time, simply starts with nothing in a world that remembers everything else.
    ///
    /// Dropped items are kept apart from UHFPS's own runtime saveables. UHFPS recreates those on whichever machine
    /// loads the save, which in a room would mean four private copies of the same key; the host re-drops them
    /// instead, through the path that makes one shared item everyone can see (<see cref="World.DroppedItems"/>).
    /// </remarks>
    public sealed class MultiplayerSave
    {
        /// <summary>The world's own state, keyed by each object's World Sync id.</summary>
        public JToken World;

        /// <summary>Seconds played across every session of this save, so the figure keeps growing.</summary>
        public float TimePlayed;

        /// <summary>Each player's data, keyed by their Unity account id.</summary>
        public readonly Dictionary<string, JToken> Players = new();

        /// <summary>Items lying in the level when the game was saved.</summary>
        public readonly List<DroppedItem> Drops = new();

        /// <summary>One item on the ground: what it is, how many, and where it lies.</summary>
        public struct DroppedItem
        {
            public string ReferenceGuid;
            public int Quantity;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public JObject ToJson(string saveId)
        {
            var players = new JObject();
            foreach (var player in Players)
            {
                players[player.Key] = player.Value;
            }

            var drops = new JArray();
            foreach (var drop in Drops)
            {
                drops.Add(new JObject
                {
                    ["guid"] = drop.ReferenceGuid,
                    ["quantity"] = drop.Quantity,
                    ["position"] = new JArray(drop.Position.x, drop.Position.y, drop.Position.z),
                    ["rotation"] = new JArray(drop.Rotation.x, drop.Rotation.y, drop.Rotation.z, drop.Rotation.w)
                });
            }

            return new JObject
            {
                ["id"] = saveId,
                ["timePlayed"] = TimePlayed,
                ["worldState"] = World ?? new JObject(),
                ["players"] = players,
                ["drops"] = drops
            };
        }

        public static MultiplayerSave FromJson(JObject state)
        {
            var save = new MultiplayerSave
            {
                World = state["worldState"],
                TimePlayed = (float?)state["timePlayed"] ?? 0f
            };

            if (state["players"] is JObject players)
            {
                foreach (var player in players)
                {
                    save.Players[player.Key] = player.Value;
                }
            }

            if (state["drops"] is JArray drops)
            {
                foreach (var drop in drops)
                {
                    var position = drop["position"] as JArray;
                    var rotation = drop["rotation"] as JArray;
                    if (position == null || position.Count != 3 || rotation == null || rotation.Count != 4) continue;

                    save.Drops.Add(new DroppedItem
                    {
                        ReferenceGuid = (string)drop["guid"],
                        Quantity = (int?)drop["quantity"] ?? 1,
                        Position = new Vector3((float)position[0], (float)position[1], (float)position[2]),
                        Rotation = new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2],
                            (float)rotation[3])
                    });
                }
            }

            return save;
        }
    }
}
