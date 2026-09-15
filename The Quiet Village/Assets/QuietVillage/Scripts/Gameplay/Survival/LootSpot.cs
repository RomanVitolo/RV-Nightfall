using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// A place loot may lie. The level holds more of these than any one game uses; each new game keeps a random share.
    /// </summary>
    /// <remarks>
    /// Sits on the pickup itself, which World Sync already replicates. Every client removes the same spots, because the
    /// choice comes from one seed the host picks (<see cref="SurvivalDirector"/>), so nothing extra travels and a removed
    /// spot is simply an item nobody can see. A save stores which items are still lying there, as for any pickup, and the
    /// seed with it, so a resumed game removes the same ones again.
    ///
    /// Spots are chosen within a group: "keep 2 of the 5 in this crypt", or "keep 1 of the 4 places this key can be".
    /// A guaranteed spot is always kept and counts towards its group.
    /// </remarks>
    [DisallowMultipleComponent]
    public class LootSpot : MonoBehaviour
    {
        [Tooltip("Spots sharing a group are chosen from together, e.g. one crypt's shelves, or every place one key may be.")]
        [SerializeField] private string m_group = "loot";

        [Tooltip("How many spots of this group each game keeps. Every spot in a group should say the same.")]
        [SerializeField, Min(0)] private int m_keep = 1;

        [Tooltip("Always kept, e.g. the scrap a level cannot be won without.")]
        [SerializeField] private bool m_guaranteed;

        public string Group => m_group;

        public int Keep => m_keep;

        public bool Guaranteed => m_guaranteed;

#if UNITY_EDITOR
        /// <summary>For the level builder, which places and groups the spots.</summary>
        public void Configure(string group, int keep, bool guaranteed)
        {
            m_group = group;
            m_keep = keep;
            m_guaranteed = guaranteed;
        }
#endif
    }
}
