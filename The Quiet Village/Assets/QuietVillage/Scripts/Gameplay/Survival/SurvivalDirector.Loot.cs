using System.Collections.Generic;
using QuietVillage.Multiplayer.Bridge.World;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The game's seed: which loot spots are kept, and each crypt padlock's code.
    /// </summary>
    /// <remarks>
    /// One number from the host decides it all, replicated with the director's spawn, and every client works out the
    /// same result from it. Sending the choice itself would mean one message per spot, or an ordering every client
    /// already agrees on; the seed needs only the ordering.
    ///
    /// That ordering is each spot's World Sync id, written into the scene at author time and the same everywhere. A new
    /// game picks a fresh seed; a resumed one takes the save's, and restarting after a wipe without a save is a new game.
    /// </remarks>
    public partial class SurvivalDirector
    {
        private const string SaveLootSeed = "lootSeed";

        private readonly NetworkVariable<int> m_lootSeed =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private int m_appliedSeed;

        /// <summary>Server: the seed for a game, from its save or new.</summary>
        private void ChooseLootSeed(Newtonsoft.Json.Linq.JToken saved)
        {
            var seed = (int?)saved?[SaveLootSeed] ?? 0;

            // Zero means "not chosen yet" on the wire, so a new seed is never zero.
            while (seed == 0) seed = Random.Range(int.MinValue, int.MaxValue);

            m_lootSeed.Value = seed;
            ApplyLootSeed(seed);
        }

        private void HandleLootSeedChanged(int previous, int current) => ApplyLootSeed(current);

        /// <summary>Removes the loot this game does without, and sets the padlock codes. Same seed, same result.</summary>
        private void ApplyLootSeed(int seed)
        {
            if (seed == 0 || seed == m_appliedSeed) return;
            m_appliedSeed = seed;

            var groups = new SortedDictionary<string, List<LootSpot>>(System.StringComparer.Ordinal);
            foreach (var spot in FindObjectsByType<LootSpot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (spot == null || spot.gameObject.scene != gameObject.scene) continue;

                if (!groups.TryGetValue(spot.Group ?? string.Empty, out var list)) groups[spot.Group ?? string.Empty] = list = new List<LootSpot>();
                list.Add(spot);
            }

            var removed = 0;
            foreach (var group in groups)
            {
                var spots = group.Value;
                spots.Sort((a, b) => string.CompareOrdinal(StableKey(a), StableKey(b)));

                var random = new System.Random(seed ^ (int)CryptPadlock.StableHash(group.Key));

                // Shuffled with the seed, then guaranteed spots moved to the front, so they are kept first.
                for (var i = spots.Count - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (spots[i], spots[j]) = (spots[j], spots[i]);
                }

                spots.Sort((a, b) => b.Guaranteed.CompareTo(a.Guaranteed));

                var keep = 0;
                foreach (var spot in spots) keep = Mathf.Max(keep, spot.Keep);
                keep = ScaledKeep(group.Key, keep, random);

                for (var i = 0; i < spots.Count; i++)
                {
                    if (i < keep || spots[i].Guaranteed) continue;

                    // Not a pickup: SyncedPickup only reports a take when its own player used the item this frame.
                    if (spots[i].gameObject.activeSelf) spots[i].gameObject.SetActive(false);
                    removed++;
                }
            }

            foreach (var padlock in FindObjectsByType<CryptPadlock>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (padlock != null && padlock.gameObject.scene == gameObject.scene) padlock.ApplySeed(seed);
            }

            if (removed > 0) Debug.Log($"{nameof(SurvivalDirector)}: this game's loot leaves out {removed} spot(s).", this);
        }

        /// <summary>The spot's World Sync id, the same on every machine; its hierarchy path if it has none.</summary>
        private static string StableKey(LootSpot spot)
        {
            var pickup = spot.GetComponent<SyncedPickup>();
            if (pickup != null && !string.IsNullOrEmpty(pickup.Id)) return pickup.Id;

            var path = spot.name;
            for (var parent = spot.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
    }
}
