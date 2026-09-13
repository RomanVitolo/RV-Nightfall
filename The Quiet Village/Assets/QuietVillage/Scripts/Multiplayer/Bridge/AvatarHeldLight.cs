using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Shines another player's flashlight, lantern or candle for this client.
    /// </summary>
    /// <remarks>
    /// A player item is only ever activated by its owner's PlayerItemsManager, so on a remote copy the item stays
    /// switched off and its light with it. In a dark game that is most of what a teammate is: the light moving ahead
    /// of you. This adds a light of its own and copies the settings of whichever item they hold, so a flashlight
    /// throws a flashlight beam and a candle a candle's glow.
    ///
    /// It hangs off the camera holder, which carries the replicated look, so the beam points where the player is
    /// looking rather than wherever their hand animation happens to be.
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> adds it to bodies this client does not own.
    /// </remarks>
    public class AvatarHeldLight : MonoBehaviour
    {
        // Out of the head and a little to the side, so the beam reads as carried rather than worn.
        private static readonly Vector3 HandOffset = new(0.18f, -0.18f, 0.15f);

        private PlayerActionSync m_actions;
        private PlayerHealthSync m_health;
        private Transform m_lookTransform;

        private Light m_light;
        private PlayerItemBehaviour m_lastItem;
        private Light m_lastSource;

        /// <summary>Starts lighting for this body. For a copy this client does not own.</summary>
        public void Bind(PlayerActionSync actions, Transform lookTransform)
        {
            m_actions = actions;
            m_lookTransform = lookTransform;
            m_health = GetComponent<PlayerHealthSync>();
        }

        private void LateUpdate()
        {
            if (m_actions == null || m_lookTransform == null) return;

            // A dead player's item stays equipped on their own client, so its light would keep shining from a corpse,
            // pointing wherever its owner now looks while spectating.
            var alive = m_health == null || !m_health.IsDead;
            var source = alive && m_actions.HeldLightOn ? SourceLight(m_actions.EquippedItem) : null;
            if (source == null)
            {
                if (m_light != null) m_light.enabled = false;
                return;
            }

            if (m_light == null) m_light = CreateLight();

            // Copied every frame, not once: a flashlight dims as its battery runs down and turns violet in UV mode.
            m_light.type = source.type;
            m_light.color = source.color;
            m_light.intensity = source.intensity;
            m_light.range = source.range;
            m_light.spotAngle = source.spotAngle;
            m_light.innerSpotAngle = source.innerSpotAngle;
            m_light.cookie = source.cookie;
            m_light.shadows = source.shadows;
            m_light.enabled = true;
        }

        private Light CreateLight()
        {
            var holder = new GameObject("AvatarHeldLight");
            holder.transform.SetParent(m_lookTransform, false);
            holder.transform.SetLocalPositionAndRotation(HandOffset, Quaternion.identity);

            return holder.AddComponent<Light>();
        }

        /// <summary>The light of the item they hold. Switched off on this copy, but its settings are still here.</summary>
        private Light SourceLight(PlayerItemBehaviour item)
        {
            if (item == null) return null;

            // Cached per item: this runs every frame, and an item's light never moves between its children.
            if (!ReferenceEquals(item, m_lastItem))
            {
                m_lastItem = item;
                m_lastSource = item.GetComponentInChildren<Light>(true);
            }

            return m_lastSource;
        }
    }
}
