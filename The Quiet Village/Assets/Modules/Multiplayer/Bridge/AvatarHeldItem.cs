using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// The item they hold is a player-item index, and <see cref="AvatarHeldItemModels"/> says which prop and grip
    /// belong to that index. Nothing new is replicated: <see cref="PlayerActionSync"/> already publishes the index
    /// for the light and the sounds, so every client resolves the same prop locally.
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> adds it to bodies this client does not own.
    /// </remarks>
    public class AvatarHeldItem : MonoBehaviour
    {
        private PlayerActionSync m_actions;
        private Inventory m_database;
        private AvatarHeldItemModels m_models;
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
            m_models = Resources.Load<AvatarHeldItemModels>(AvatarHeldItemModels.ResourcePath);

            if (m_hand == null)
                Debug.LogWarning($"{nameof(AvatarHeldItem)}: the body has no right hand bone, so held items " +
                                 "will not be shown. Is the avatar rig still Humanoid?", this);

            if (m_models == null)
                Debug.LogWarning($"{nameof(AvatarHeldItem)}: no {nameof(AvatarHeldItemModels)} asset in Resources, " +
                                 "so teammates only show items the inventory database gives a pickup model. " +
                                 "Run Tools > Multiplayer > Set Up Held Item Models.", this);
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

            if (item == null) return;

            var itemIndex = m_actions.EquippedItemIndex;
            GameObject source;
            Vector3 gripPosition;
            Vector3 gripRotation;

            if (m_models != null && m_models.TryGet(itemIndex, out var entry))
            {
                source = entry.Model;
                gripPosition = entry.GripPosition;
                gripRotation = entry.GripRotation;
            }
            else
            {
                source = DatabaseModelFor(itemIndex);
                gripPosition = AvatarHeldItemModels.DefaultGripPosition;
                gripRotation = AvatarHeldItemModels.DefaultGripRotation;
            }

            if (source == null) return;

            m_model = CopyVisuals(source.transform, m_hand, m_hand.gameObject.layer);
            m_model.name = $"HeldItem ({source.name})";
            m_model.transform.SetLocalPositionAndRotation(gripPosition, Quaternion.Euler(gripRotation));
            m_model.transform.localScale = UndoParentScale(source.transform.localScale, m_hand.lossyScale);
        }

        /// <summary>
        /// Rebuilds a prefab as bare meshes, without instantiating it.
        /// </summary>
        /// <remarks>
        /// Instantiating a pickup would run its <c>Awake</c> and <c>OnEnable</c> before anything could strip it: an
        /// <c>InteractableItem</c> that registers itself, a rigidbody that falls out of the hand, colliders the player
        /// walks into, a second light next to the one <see cref="AvatarHeldLight"/> already shines. Copying only mesh
        /// and material means none of that ever exists.
        ///
        /// The copies take the body's layer, so the prop is lit and culled exactly like the hand holding it.
        /// </remarks>
        private static GameObject CopyVisuals(Transform source, Transform parent, int layer)
        {
            var copy = new GameObject(source.name) { layer = layer };
            copy.transform.SetParent(parent, false);
            copy.transform.SetLocalPositionAndRotation(source.localPosition, source.localRotation);
            copy.transform.localScale = source.localScale;

            var sourceFilter = source.GetComponent<MeshFilter>();
            var sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceFilter != null && sourceFilter.sharedMesh != null && sourceRenderer != null && sourceRenderer.enabled)
            {
                copy.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;

                var renderer = copy.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.shadowCastingMode = ShadowCastingMode.On;
            }

            foreach (Transform child in source)
            {
                if (child.gameObject.activeSelf) CopyVisuals(child, copy.transform, layer);
            }

            return copy;
        }

        /// <summary>
        /// The local scale that gives a prop its prefab size under a scaled parent.
        /// </summary>
        /// <remarks>
        /// Imported rigs often carry scale on their bones (a centimetre FBX puts 100 or 0.01 on the armature), and a
        /// prop parented under that without correction is shown microscopic or the size of a car.
        /// </remarks>
        private static Vector3 UndoParentScale(Vector3 scale, Vector3 parentScale) => new(
            Mathf.Approximately(parentScale.x, 0f) ? scale.x : scale.x / parentScale.x,
            Mathf.Approximately(parentScale.y, 0f) ? scale.y : scale.y / parentScale.y,
            Mathf.Approximately(parentScale.z, 0f) ? scale.z : scale.z / parentScale.z);

        /// <summary>
        /// The floor model UHFPS's own database names for this player item, or <c>null</c> if it names none.
        /// </summary>
        /// <remarks>Fallback for items missing from <see cref="AvatarHeldItemModels"/>, e.g. ones added since it was built.</remarks>
        private GameObject DatabaseModelFor(int itemIndex)
        {
            if (itemIndex < 0 || m_database == null || m_database.inventoryDatabase == null) return null;

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
