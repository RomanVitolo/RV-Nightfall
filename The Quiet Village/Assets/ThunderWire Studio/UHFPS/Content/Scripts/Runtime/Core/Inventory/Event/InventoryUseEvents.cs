using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Events;
using ThunderWire.Attributes;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Inventory Use Events")]
    public class InventoryUseEvents : MonoBehaviour
    {
        [Serializable]
        public struct UseEvent
        {
            public ItemGuid Item;
            [Space]
            public UnityEvent<ItemUseEvent> OnUse;
        }

        public List<UseEvent> UseEvents = new();

        // MULTIPLAYER PATCH: the inventory lives on the local player, which Netcode spawns after this object has
        // started. Registering in Start found no inventory, so these use events never fired; they now register
        // once the player exists.
        private void Start()
        {
            if (LocalPlayerContext.IsReady) RegisterUseEvents();
            else LocalPlayerContext.Ready += HandlePlayerReady;
        }

        private void OnDestroy()
        {
            LocalPlayerContext.Ready -= HandlePlayerReady;
        }

        private void HandlePlayerReady()
        {
            LocalPlayerContext.Ready -= HandlePlayerReady;
            RegisterUseEvents();
        }

        private void RegisterUseEvents()
        {
            Inventory inventory = LocalPlayerContext.Inventory;
            if (inventory == null) return;

            foreach (var evt in UseEvents)
            {
                inventory.RegisterUseEvent(evt.Item, act => evt.OnUse?.Invoke(act));
            }
        }
    }
}