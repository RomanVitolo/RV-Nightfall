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

        private static T GetOrAdd<T>(GameObject target) where T : Component =>
            target.TryGetComponent(out T existing) ? existing : target.AddComponent<T>();
    }
}
