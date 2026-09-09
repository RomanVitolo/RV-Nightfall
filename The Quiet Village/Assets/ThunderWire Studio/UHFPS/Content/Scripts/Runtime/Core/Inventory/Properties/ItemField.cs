using System;
using UnityEngine;
using UHFPS.Tools;
using static UHFPS.Scriptable.InventoryDatabase;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [Serializable]
    public abstract class ItemField
    {
        [Serializable]
        public struct ItemRef
        {
            public string Name;
            public string GUID;
            public Texture2D Icon;

            public ItemRef(Item item)
            {
                Name = item.Title;
                GUID = item.GUID;
                Icon = item.Icon != null
                    ? item.Icon.texture : null;
            }
        }

        [Serializable]
        public struct SectionRef
        {
            public string Name;
            public string GUID;

            public SectionRef(Section section)
            {
                Name = section.Name;
                GUID = section.GUID;
            }
        }

        [SerializeField]
        private string m_GUID;
        public string GUID
        {
            get => m_GUID;
            set
            {
                m_GUID = value;
                Refresh();
            }
        }

        public void Refresh(bool withError = false)
        {
            if (!(LocalPlayerContext.Inventory != null) || GUID.IsEmpty())
                return;

            if (LocalPlayerContext.Inventory.inventoryDatabase == null)
                throw new NullReferenceException("Inventory database asset is not assigned!");

            if (LocalPlayerContext.Inventory.inventoryDatabase.TryGetItemWithSection(GUID, out Section section, out Item item))
            {
                m_Item = new(item);
                m_Section = new(section);
            }
            else if (withError)
            {
                Debug.LogError("Could not find item with GUID: " + GUID);
            }
        }

        [SerializeField]
        private ItemRef m_Item;
        public ItemRef Item
        {
            get
            {
                if (m_Item.GUID.IsEmpty() && !GUID.IsEmpty())
                    Refresh();

                return m_Item;
            }
        }

        [SerializeField]
        private SectionRef m_Section;
        public SectionRef Section
        {
            get
            {
                if (m_Section.GUID.IsEmpty() && !GUID.IsEmpty())
                    Refresh();

                return m_Section;
            }
        }

        /// <summary>
        /// Returns the quantity of the Inventory Item.
        /// </summary>
        public int Quantity
        {
            get => !GUID.IsEmpty() ? LocalPlayerContext.Inventory.GetItemQuantity(GUID) : 0;
        }

        /// <summary>
        /// Check if the item is in Inventory.
        /// </summary>
        public bool InInventory
        {
            get => !GUID.IsEmpty()
                   && (LocalPlayerContext.Inventory != null)
                   && LocalPlayerContext.Inventory.ContainsItem(GUID);
        }

        /// <summary>
        /// Get item reference from Inventory (Runtime).
        /// </summary>
        public Item GetItem()
        {
            if (!GUID.IsEmpty() && (LocalPlayerContext.Inventory != null))
            {
                if (LocalPlayerContext.Inventory.items.TryGetValue(GUID, out Item item))
                    return item;
            }

            return null;
        }

        /// <summary>
        /// Get item directly from Inventory Database.
        /// </summary>
        public Item GetItemRaw()
        {
            if (!GUID.IsEmpty() && (LocalPlayerContext.Inventory != null) && LocalPlayerContext.Inventory.inventoryDatabase != null)
            {
                if(LocalPlayerContext.Inventory.inventoryDatabase.TryGetItemByGUID(GUID, out Item item))
                {
                    return item;
                }
            }

            return null;
        }

        public static implicit operator string(ItemField item)
        {
            return item.GUID;
        }
    }
}