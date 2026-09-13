using System;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Keeps what the local player carries in play for everyone else: dropped where they fall when they die,
    /// and known to the host in case they leave.
    /// </summary>
    /// <remarks>
    /// Death is permanent until the level restarts, and only this client has the inventory, so a key in a dead
    /// player's pocket would leave the level unfinishable. On death this drops every item that has a drop object,
    /// through the same path as dropping one by hand (<see cref="DroppedItems"/>), and empties the inventory.
    ///
    /// A player who disconnects cannot drop anything, so the host is kept told what this client carries and drops
    /// it on their behalf (<see cref="WorldSync"/>). After a death that list is empty, so nothing drops twice.
    ///
    /// Local player only: <see cref="HeroPlayerNetworkSetup"/> adds it to the owner's copy.
    /// </remarks>
    public class CarriedItems : MonoBehaviour
    {
        private Inventory m_inventory;
        private PlayerHealthSync m_health;
        private WorldSync m_world;

        private IDisposable m_addedOrRemoved;
        private IDisposable m_stackChanged;
        private bool m_dirty;
        private string m_reported;
        private bool m_droppedOnDeath;

        /// <summary>Starts following the local player's inventory.</summary>
        public void Bind(Inventory inventory, PlayerHealthSync health)
        {
            m_inventory = inventory;
            m_health = health;

            m_addedOrRemoved = inventory.OnInventoryChanged.Subscribe(_ => m_dirty = true);
            m_stackChanged = inventory.OnStackChanged.Subscribe(_ => m_dirty = true);

            // Starting items were added before this existed.
            m_dirty = true;
        }

        private void OnDestroy()
        {
            m_addedOrRemoved?.Dispose();
            m_stackChanged?.Dispose();
        }

        private void Update()
        {
            if (m_droppedOnDeath || m_health == null || !m_health.IsDead) return;

            m_droppedOnDeath = true;
            DropEverything();
        }

        private void LateUpdate()
        {
            // Once a frame at most, however many changes the frame made; and only when the list actually differs.
            if (!m_dirty || !TryGetWorld(out var world)) return;

            m_dirty = false;

            var carried = DroppedItems.Encode(DroppedItems.Droppable(m_inventory).ConvertAll(entry => entry.Item));
            if (carried == m_reported) return;

            m_reported = carried;
            world.ReportCarried(carried);
        }

        private void DropEverything()
        {
            if (!TryGetWorld(out var world)) return;

            // Taken before anything is removed: removing changes the collection being read.
            var droppable = DroppedItems.Droppable(m_inventory);
            var feet = transform.position;

            for (var i = 0; i < droppable.Count; i++)
            {
                var (entry, item) = droppable[i];
                var pose = DroppedItems.ScatterPose(feet, i, droppable.Count);

                var dropped = DroppedItems.CreateCopy(item.ReferenceGuid, item.Quantity, pose.position, pose.rotation);
                if (dropped == null) continue;

                world.PublishDrop(dropped, item.ReferenceGuid, item.Quantity);
                m_inventory.RemoveItem(entry);
            }

            if (droppable.Count > 0)
                Debug.Log($"{nameof(CarriedItems)}: dropped {droppable.Count} item(s) where the local player died.", this);
        }

        private bool TryGetWorld(out WorldSync world)
        {
            // The level's WorldSync, found on first need: it lives in the level scene, this on the player.
            if (m_world == null) m_world = FindAnyObjectByType<WorldSync>();

            world = m_world;
            return world != null && world.IsSpawned;
        }
    }
}
