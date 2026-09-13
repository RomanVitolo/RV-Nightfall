using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>How the director chooses a body for each creature it spawns.</summary>
    public enum CreatureSelection
    {
        /// <summary>A random enabled body per creature, so one night can mix several.</summary>
        Mixed,

        /// <summary>One enabled body after another, in catalog order.</summary>
        Cycle,

        /// <summary>Only the body named by <see cref="CreatureCatalog.OnlyId"/>, for trying one out.</summary>
        OnlyOne
    }

    /// <summary>
    /// The bodies a night creature can wear: which model, which clips, and how it moves and strikes.
    /// </summary>
    /// <remarks>
    /// The creature's behaviour stays one thing, <see cref="NightCreature"/>: chase what it can reach, break what is in
    /// the way. A body only changes how that looks and how fast and how far it hits, so the catalog can hold models from
    /// any pack, Generic or Humanoid, side by side.
    ///
    /// Every body plays the same state machine (<c>CreatureBase.controller</c>) through its own
    /// <see cref="AnimatorOverrideController"/>, which swaps in the pack's clips. <c>Tools > Quiet Village > Creatures >
    /// Set Up Creatures</c> fills entries from the packs and builds those overrides; afterwards the catalog is design
    /// data, edited in its Inspector.
    ///
    /// Bodies are identified on the network by list index, so reordering the list while a room is playing would dress
    /// creatures differently on different machines. Every machine loads the same asset, so between sessions it is safe.
    /// </remarks>
    [CreateAssetMenu(fileName = "CreatureCatalog", menuName = "Survival/Creature Catalog")]
    public class CreatureCatalog : ScriptableObject
    {
        [Serializable]
        public class MaterialSwap
        {
            [Tooltip("Replaces every material whose name contains this text, e.g. \"Body\".")]
            public string NameContains;

            public Material Replacement;
        }

        [Serializable]
        public class Body
        {
            [Tooltip("Stable name, used by Only One and in reports.")]
            public string Id;

            public string DisplayName;

            [Tooltip("Unticked bodies are never chosen.")]
            public bool Enabled = true;

            [Tooltip("Model prefab from the pack. Its own Animator, if any, is reused and reconfigured.")]
            public GameObject Prefab;

            [Tooltip("Override of CreatureBase.controller with the clips below. Built by the setup tool; re-run it after " +
                     "changing clips.")]
            public RuntimeAnimatorController Controller;

            [Header("Clips (the setup tool builds Controller from these)")]
            public AnimationClip Idle;
            public AnimationClip Walk;

            [Tooltip("Empty uses the walk, played faster.")]
            public AnimationClip Run;

            [Tooltip("Up to three; fewer are repeated.")]
            public List<AnimationClip> Attacks = new();

            [Tooltip("Played at dawn. Empty removes the creature straight away.")]
            public AnimationClip Death;

            [Tooltip("Played on arrival, e.g. a shout. Empty skips it.")]
            public AnimationClip Entrance;

            [Tooltip("Only for Humanoid clips on a different rig; leave empty to keep the model's own Avatar.")]
            public Avatar Avatar;

            [Min(0.05f)] public float Scale = 1f;

            [Tooltip("Degrees to turn the model if it was authored facing another way than +Z.")]
            public float YawOffset;

            public List<MaterialSwap> MaterialSwaps = new();

            [Header("Movement")]
            [Tooltip("NavMeshAgent speed (m/s) while hunting.")]
            [Min(0.1f)] public float MoveSpeed = 3.4f;

            [Tooltip("Ground speed (m/s) the walk clip looks right at. Playback is scaled from it.")]
            [Min(0.1f)] public float WalkClipSpeed = 1.4f;

            [Tooltip("Ground speed (m/s) the run clip looks right at.")]
            [Min(0.1f)] public float RunClipSpeed = 4f;

            [Header("Attack")]
            [Tooltip("Seconds into the swing when the blow lands. The target must still be in reach then.")]
            [Min(0f)] public float AttackHitDelay = 0.4f;

            [Tooltip("Seconds the body stands still for a swing.")]
            [Min(0f)] public float AttackDuration = 1f;

            [Header("Arrival and leaving")]
            [Tooltip("Plays an entrance (a shout, rising from the ground) when the creature appears.")]
            public bool HasEntrance;

            [Tooltip("Seconds the death clip gets at dawn before the creature is removed.")]
            [Min(0f)] public float DeathDuration = 3f;
        }

        [SerializeField] private CreatureSelection m_selection = CreatureSelection.Mixed;

        [Tooltip("Id of the body Only One uses.")]
        [SerializeField] private string m_onlyId;

        [SerializeField] private List<Body> m_bodies = new();

        public IReadOnlyList<Body> Bodies => m_bodies;

        public CreatureSelection Selection => m_selection;

        public string OnlyId => m_onlyId;

        /// <summary>The body at a network index, or <c>null</c> for none (the placeholder capsule).</summary>
        public Body At(int index) =>
            index >= 0 && index < m_bodies.Count && IsUsable(m_bodies[index]) ? m_bodies[index] : null;

        /// <summary>Server: the body index for the <paramref name="spawnNumber"/>th creature of a night; -1 if none is usable.</summary>
        /// <param name="forcedIndex">A body picked by the host's test key, which wins over the selection mode; -1 for none.</param>
        public int Pick(int spawnNumber, int forcedIndex = -1)
        {
            if (At(forcedIndex) != null) return forcedIndex;

            var enabled = EnabledIndices();
            if (enabled.Count == 0) return -1;

            switch (m_selection)
            {
                case CreatureSelection.OnlyOne:
                    for (var i = 0; i < m_bodies.Count; i++)
                    {
                        if (IsUsable(m_bodies[i]) && m_bodies[i].Id == m_onlyId) return i;
                    }

                    // A mistyped id should not empty the night: fall back to the first enabled body.
                    return enabled[0];

                case CreatureSelection.Cycle:
                    return enabled[spawnNumber % enabled.Count];

                default:
                    return enabled[UnityEngine.Random.Range(0, enabled.Count)];
            }
        }

        /// <summary>The next usable body after <paramref name="index"/>, wrapping, for the host's test key; enabled or not.</summary>
        public int NextUsable(int index)
        {
            for (var step = 1; step <= m_bodies.Count; step++)
            {
                var candidate = ((index < 0 ? -1 : index) + step) % m_bodies.Count;
                if (IsUsable(m_bodies[candidate])) return candidate;
            }

            return -1;
        }

        private List<int> EnabledIndices()
        {
            var indices = new List<int>();
            for (var i = 0; i < m_bodies.Count; i++)
            {
                if (IsUsable(m_bodies[i]) && m_bodies[i].Enabled) indices.Add(i);
            }

            return indices;
        }

        private static bool IsUsable(Body body) => body != null && body.Prefab != null && body.Controller != null;

#if UNITY_EDITOR
        public List<Body> EditableBodies => m_bodies;
#endif
    }
}
