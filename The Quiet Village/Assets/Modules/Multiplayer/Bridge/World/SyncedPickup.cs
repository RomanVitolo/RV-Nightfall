using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Makes a pickup disappear for everyone when one player takes it — and only one player can.
    /// </summary>
    /// <remarks>
    /// No UHFPS change needed. InteractController calls every IInteractStart on the object it is using, which
    /// includes this one, immediately before it hands the item to the inventory; UHFPS then deactivates the
    /// item in that same frame. So "interacted this frame, then disabled" means this client just took it.
    ///
    /// The take is optimistic: the item lands in this player's inventory at once, as in single player, and
    /// the server confirms. If another player's request reached the server first, this client's copy is a
    /// duplicate and is removed again — a rare race, but otherwise two players could each hold the only key.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SyncedPickup : WorldSyncEntity, IInteractStart
    {
        [SerializeField] private InteractableItem m_item;

        private const string LostRaceHint = "Someone else took it.";

        private int m_localInteractFrame = -1;
        private bool m_locallyExamining;
        private bool m_taken;

        /// <summary>Destroy rather than deactivate when another player takes it: set on dropped items.</summary>
        internal bool DestroyWhenTaken { get; set; }

        private void Awake()
        {
            if (m_item == null) m_item = GetComponent<InteractableItem>();
        }

        private void OnEnable()
        {
            if (m_item == null) return;

            m_item.OnExamineStartEvent?.AddListener(HandleExamineStarted);
            m_item.OnExamineEndEvent?.AddListener(HandleExamineEnded);
        }

        /// <summary>Called by InteractController, before it gives the item to the inventory.</summary>
        public void InteractStart()
        {
            m_localInteractFrame = Time.frameCount;
        }

        private void HandleExamineStarted() => m_locallyExamining = true;

        private void HandleExamineEnded() => m_locallyExamining = false;

        private void OnDisable()
        {
            if (m_item != null)
            {
                m_item.OnExamineStartEvent?.RemoveListener(HandleExamineStarted);
                m_item.OnExamineEndEvent?.RemoveListener(HandleExamineEnded);
            }

            // Two ways UHFPS takes an item, both deactivating it: using it directly (IInteractStart fires that
            // same frame), or "take" while examining it — which goes straight to the inventory without any
            // interact callback and without ending the examine. Being disabled at any other time (a puzzle
            // hiding the item, the level unloading) is not a pickup.
            var takenByThisClient = m_localInteractFrame == Time.frameCount || m_locallyExamining;
            m_locallyExamining = false;

            if (m_taken || !takenByThisClient || !IsLive) return;

            m_taken = true;
            World.RequestTake(this);
        }

        internal override void ApplyRemoteTaken()
        {
            m_taken = true;

            // A dropped item exists only because someone dropped it, so it goes, as UHFPS would have it.
            if (DestroyWhenTaken)
            {
                Destroy(gameObject);
                return;
            }

            // Deactivated rather than destroyed even when UHFPS would destroy it: harmless either way for a
            // scene object, and it keeps the entity registered should the id ever be referenced again.
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        internal override void OnTakeDenied()
        {
            if (m_item == null) return;

            var inventory = LocalPlayerContext.Inventory;
            if (m_item.InteractableType == InteractableItem.InteractableTypeEnum.InventoryItem
                && inventory != null && m_item.PickupItem != null && !string.IsNullOrEmpty(m_item.PickupItem.GUID))
            {
                inventory.RemoveItem(m_item.PickupItem.GUID, m_item.Quantity);
            }

            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(LostRaceHint, 2f);
        }
    }
}
