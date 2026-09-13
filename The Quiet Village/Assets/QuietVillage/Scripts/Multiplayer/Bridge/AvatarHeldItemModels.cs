using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Which prop a teammate's body carries for each player item, and how that prop sits in the hand.
    /// </summary>
    /// <remarks>
    /// UHFPS's inventory database has a field for an item's pickup model (<c>Item.ItemObject</c>), but the demo
    /// database leaves it empty for every item, so a teammate's hand had nothing to show. Filling that field would
    /// edit a vendor asset that a UHFPS update overwrites; this bridge asset is ours to keep.
    ///
    /// It also gives every item its own grip. The pickup models share no common orientation, so a single offset
    /// for all of them puts at least some items in the hand sideways.
    ///
    /// Built by Tools > Quiet Village > Multiplayer > Set Up Held Item Models. Loaded from Resources because
    /// <see cref="AvatarHeldItem"/> is added at runtime and has no Inspector to be wired through.
    /// </remarks>
    public class AvatarHeldItemModels : ScriptableObject
    {
        /// <summary>The asset's name under a Resources folder.</summary>
        public const string ResourcePath = "AvatarHeldItemModels";

        /// <summary>The grip a newly listed item starts with, before it is tuned.</summary>
        public static readonly Vector3 DefaultGripPosition = new(0f, 0f, 0.04f);

        /// <inheritdoc cref="DefaultGripPosition"/>
        public static readonly Vector3 DefaultGripRotation = new(0f, 90f, 90f);

        [Serializable]
        public class Entry
        {
            [Tooltip("The inventory item's name. For reading this list only; matching uses the index.")]
            public string Title;

            [Tooltip("Index into PlayerItemsManager's PlayerItems, the value PlayerActionSync replicates.")]
            public int PlayerItemIndex = -1;

            [Tooltip("Prefab whose meshes are shown in the hand. Only its renderers are copied.")]
            public GameObject Model;

            [Tooltip("Offset from the right hand bone, in the hand's space.")]
            public Vector3 GripPosition = DefaultGripPosition;

            [Tooltip("Rotation from the right hand bone, in degrees.")]
            public Vector3 GripRotation = DefaultGripRotation;
        }

        [SerializeField] private List<Entry> m_entries = new();

        /// <summary>Every listed item. Edited by the setup tool; read-only at runtime.</summary>
        public List<Entry> Entries => m_entries;

        /// <summary>Finds the entry for a player item.</summary>
        /// <returns><c>false</c> if the item is not listed or has no model.</returns>
        public bool TryGet(int playerItemIndex, out Entry entry)
        {
            entry = null;
            if (playerItemIndex < 0) return false;

            foreach (var candidate in m_entries)
            {
                if (candidate == null || candidate.PlayerItemIndex != playerItemIndex) continue;
                if (candidate.Model == null) return false;

                entry = candidate;
                return true;
            }

            return false;
        }
    }
}
