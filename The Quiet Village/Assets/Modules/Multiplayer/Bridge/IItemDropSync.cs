using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Told about each item the local player drops from the inventory, so it can exist for every player.
    /// </summary>
    /// <remarks>
    /// UHFPS creates a dropped item on the dropping client only. Nothing about it was ever in the level, so
    /// World Sync has no id for it until it is given one.
    /// </remarks>
    public interface IItemDropSync
    {
        /// <summary>Called once UHFPS has created and launched the dropped object.</summary>
        /// <param name="dropped">The new object, on this client.</param>
        /// <param name="referenceGuid">Its ObjectReference GUID, which every client can instantiate.</param>
        /// <param name="quantity">How many of the item it holds.</param>
        void OnItemDropped(GameObject dropped, string referenceGuid, int quantity);
    }
}
