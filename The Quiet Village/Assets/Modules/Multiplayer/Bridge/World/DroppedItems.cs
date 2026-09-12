using System.Collections.Generic;
using System.Linq;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Makes an item dropped from the inventory exist for every player, and be taken by only one of them.
    /// </summary>
    /// <remarks>
    /// UHFPS creates the dropped object on the dropping client only. That client asks the host for World Sync
    /// keys, and the host tells everyone which object to create, where. Each copy then joins World Sync like any
    /// scene pickup: first taker wins, examining is exclusive, and the dropper's physics is the one that counts
    /// while the item falls.
    ///
    /// Nobody can join a started game, so there are no late joiners to show earlier drops to; a level restart
    /// clears them with everything else.
    ///
    /// The same path drops a whole inventory when its player dies (<see cref="CarriedItems"/>) or leaves
    /// (<see cref="WorldSync"/>), so nothing a level needs goes out of play with them.
    /// </remarks>
    public sealed class DroppedItems : IItemDropSync
    {
        /// <summary>Keys a drop takes, one per entity, in the order of <see cref="AttachEntities"/>.</summary>
        internal const int KeyCount = 3;

        internal const int PickupSlot = 0;
        internal const int MotionSlot = 1;
        internal const int ExamineSlot = 2;

        private WorldSync m_world;

        public void OnItemDropped(GameObject dropped, string referenceGuid, int quantity)
        {
            // The level's WorldSync, found on first need: it lives in the level scene, this with the player.
            if (m_world == null) m_world = Object.FindAnyObjectByType<WorldSync>();
            if (m_world == null || dropped == null || string.IsNullOrEmpty(referenceGuid)) return;

            m_world.PublishDrop(dropped, referenceGuid, quantity);
        }

        /// <summary>Creates another client's drop here, set up the way UHFPS set up the original.</summary>
        /// <returns>The copy, or <c>null</c> if the reference is unknown to this build.</returns>
        internal static GameObject CreateCopy(string referenceGuid, int quantity, Vector3 position, Quaternion rotation)
        {
            var copy = SaveGameManager.InstantiateSaveable(referenceGuid, position, rotation.eulerAngles);
            if (copy == null)
            {
                Debug.LogWarning($"{nameof(DroppedItems)}: another player dropped an item this client cannot create " +
                                 $"(ObjectReference {referenceGuid}).");
                return null;
            }

            // Dynamic, as UHFPS makes a drop. The dropper's stream holds it still until it says where it goes, and
            // hands it back to physics once it lands. No launch force here: the dropper's copy supplies the flight.
            if (copy.TryGetComponent(out Rigidbody body))
            {
                body.useGravity = true;
                body.isKinematic = false;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }

            if (copy.TryGetComponent(out InteractableItem interactable))
            {
                interactable.DisableType = InteractableItem.DisableTypeEnum.Destroy;
                interactable.Quantity = (ushort)Mathf.Clamp(quantity, 0, ushort.MaxValue);
            }

            return copy;
        }

        /// <summary>
        /// Adds what a drop replicates through, the same set Set Up World Sync gives a scene pickup.
        /// </summary>
        /// <returns><see cref="KeyCount"/> slots; a slot is null when the item has nothing to replicate there.</returns>
        internal static WorldSyncEntity[] AttachEntities(GameObject dropped)
        {
            var entities = new WorldSyncEntity[KeyCount];

            var pickup = GetOrAdd<SyncedPickup>(dropped);
            pickup.DestroyWhenTaken = true;
            entities[PickupSlot] = pickup;

            // Before the examine lock, which finds the object's motion entity in its Awake.
            if (dropped.TryGetComponent<Rigidbody>(out _)) entities[MotionSlot] = GetOrAdd<SyncedRigidbody>(dropped);

            if (dropped.TryGetComponent(out InteractableItem item) && item.ExamineType != InteractableItem.ExamineTypeEnum.None)
                entities[ExamineSlot] = GetOrAdd<SyncedExamineLock>(dropped);

            return entities;
        }

        /// <summary>
        /// Where the <paramref name="index"/>th of <paramref name="count"/> items goes when a player's whole inventory
        /// falls at <paramref name="feet"/>: a small ring at chest height, so they drop apart rather than into each other.
        /// </summary>
        internal static Pose ScatterPose(Vector3 feet, int index, int count)
        {
            var facing = Quaternion.Euler(0f, count > 0 ? index * 360f / count : 0f, 0f);
            var offset = count > 1 ? facing * Vector3.forward * 0.35f : Vector3.zero;
            return new Pose(feet + Vector3.up * 0.8f + offset, facing);
        }

        /// <summary>One entry of what a player carries, as much as another client needs to drop it for them.</summary>
        internal readonly struct CarriedItem
        {
            public readonly string ReferenceGuid;
            public readonly int Quantity;

            public CarriedItem(string referenceGuid, int quantity)
            {
                ReferenceGuid = referenceGuid;
                Quantity = quantity;
            }
        }

        /// <summary>The droppable items in an inventory; items with no drop object cannot fall and are left out.</summary>
        internal static List<(InventoryItem Entry, CarriedItem Item)> Droppable(Inventory inventory)
        {
            var droppable = new List<(InventoryItem, CarriedItem)>();
            if (inventory == null || inventory.carryingItems == null) return droppable;

            foreach (var entry in inventory.carryingItems.Keys)
            {
                var guid = entry != null && entry.Item != null && entry.Item.ItemObject != null
                    ? entry.Item.ItemObject.GUID
                    : null;

                if (!string.IsNullOrEmpty(guid)) droppable.Add((entry, new CarriedItem(guid, entry.Quantity)));
            }

            return droppable;
        }

        // ObjectReference GUIDs are hex, so neither separator can occur inside one.
        internal static string Encode(IEnumerable<CarriedItem> items) =>
            string.Join(";", items.Select(item => $"{item.ReferenceGuid}:{item.Quantity}"));

        internal static List<CarriedItem> Decode(string encoded)
        {
            var items = new List<CarriedItem>();
            if (string.IsNullOrEmpty(encoded)) return items;

            foreach (var entry in encoded.Split(';'))
            {
                var parts = entry.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var quantity) && quantity > 0)
                    items.Add(new CarriedItem(parts[0], quantity));
            }

            return items;
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component =>
            target.TryGetComponent(out T existing) ? existing : target.AddComponent<T>();
    }
}
