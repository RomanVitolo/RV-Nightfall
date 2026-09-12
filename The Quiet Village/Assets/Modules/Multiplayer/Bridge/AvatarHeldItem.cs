using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Puts whatever another player is holding into their body's hand, so you can see it from across the room.
    /// </summary>
    /// <remarks>
    /// Not the item they actually hold: a UHFPS player item is a first-person view model, rigged to arms and sleeves
    /// that belong in front of a camera rather than on a body. Its pickup model is used instead, the same object that
    /// lies on the floor before anyone takes it, so the body carries a clean prop at world scale.
    ///
    /// The item they hold is a player-item index, and the database says which inventory item equips that index, which
    /// in turn names the pickup model. Nothing new is replicated: <see cref="PlayerActionSync"/> already publishes the
    /// index for the light and the sounds.
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> adds it to bodies this client does not own.
    /// </remarks>
    public class AvatarHeldItem : MonoBehaviour
    {
        // First guesses at how a prop sits in the hand: the pickup models have no common grip, and this cannot be
        // judged without playing. Tune here if something is held through its own barrel.
        private static readonly Vector3 GripPosition = new(0f, 0f, 0.04f);
        private static readonly Vector3 GripRotation = new(0f, 90f, 90f);

        private PlayerActionSync m_actions;
        private Inventory m_database;
        private Transform m_hand;

        private GameObject m_model;
        private PlayerItemBehaviour m_shown;

        /// <summary>Starts carrying items on this body. For a copy this client does not own.</summary>
        /// <param name="actions">Publishes which item this player holds.</param>
        /// <param name="avatar">The body's animator; its right hand carries the prop.</param>
        /// <param name="database">Any inventory, for the item database it points at. They all share one asset.</param>
        public void Bind(PlayerActionSync actions, Animator avatar, Inventory database)
        {
            m_actions = actions;
            m_database = database;
            m_hand = avatar != null ? avatar.GetBoneTransform(HumanBodyBones.RightHand) : null;

            if (m_hand == null)
                Debug.LogWarning($"{nameof(AvatarHeldItem)}: the body has no right hand bone, so held items " +
                                 "will not be shown. Is the avatar rig still Humanoid?", this);
        }

        private void Update()
        {
            if (m_actions == null || m_hand == null) return;

            var held = m_actions.EquippedItem;
            if (ReferenceEquals(held, m_shown)) return;

            m_shown = held;
            Show(held);
        }

        private void OnDestroy()
        {
            if (m_model != null) Destroy(m_model);
        }

        private void Show(PlayerItemBehaviour item)
        {
            if (m_model != null) Destroy(m_model);
            m_model = null;

            var pickup = PickupModelFor(item);
            if (pickup == null) return;

            m_model = Instantiate(pickup, m_hand);
            m_model.name = $"HeldItem ({pickup.name})";
            m_model.transform.SetLocalPositionAndRotation(GripPosition, Quaternion.Euler(GripRotation));

            StripToVisuals(m_model);
        }

        /// <summary>
        /// Leaves the model with nothing but its looks. A pickup carries a rigidbody, colliders and the interaction
        /// that puts it in an inventory; in a hand all of that would be picked up, walked into, or fall out.
        /// </summary>
        private static void StripToVisuals(GameObject model)
        {
            foreach (var behaviour in model.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null) Destroy(behaviour);
            }

            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null) Destroy(collider);
            }

            // After its colliders: a rigidbody removed first would leave them briefly loose in the world.
            foreach (var body in model.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body != null) Destroy(body);
            }

            // A pickup's own light would double the one the bridge already shines for a held flashlight.
            foreach (var light in model.GetComponentsInChildren<Light>(true))
            {
                if (light != null) Destroy(light);
            }

            foreach (var audioSource in model.GetComponentsInChildren<AudioSource>(true))
            {
                if (audioSource != null) Destroy(audioSource);
            }
        }

        /// <summary>The floor model of the inventory item that equips this player item, or <c>null</c> if it has none.</summary>
        private GameObject PickupModelFor(PlayerItemBehaviour item)
        {
            if (item == null || m_database == null || m_database.inventoryDatabase == null) return null;

            var itemIndex = m_actions.EquippedItemIndex;
            if (itemIndex < 0) return null;

            foreach (var section in m_database.inventoryDatabase.Sections)
            {
                foreach (var entry in section.Items)
                {
                    if (entry == null || entry.UsableSettings.usableType != UsableType.PlayerItem) continue;
                    if (entry.UsableSettings.playerItemIndex != itemIndex) continue;

                    return entry.ItemObject != null ? entry.ItemObject.Object : null;
                }
            }

            return null;
        }
    }
}
