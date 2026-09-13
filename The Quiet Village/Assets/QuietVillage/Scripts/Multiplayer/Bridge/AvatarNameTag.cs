using TMPro;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Floats another player's name above their body, so you know who you are looking at.
    /// </summary>
    /// <remarks>
    /// World-space text rather than a screen overlay, and drawn as ordinary geometry, so a wall between you and
    /// them hides it: in a dark game, names glowing through walls would give away more than the game should.
    /// It fades out with distance for the same reason, and disappears entirely when its player dies, leaving a
    /// body on the floor rather than a labelled one.
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> adds it to bodies this client does not own.
    /// </remarks>
    public class AvatarNameTag : MonoBehaviour
    {
        private const float HeadClearance = 0.35f;
        private const float FallbackHeight = 1.9f;

        // Legible across a room, gone by the far end of one.
        private const float FullyVisibleRange = 12f;
        private const float FadeOutRange = 25f;

        private PlayerHealthSync m_health;
        private HeroPlayerNetworkSetup m_player;
        private Transform m_anchor;
        private TextMeshPro m_label;
        private string m_shownName;

        /// <summary>Starts labelling this body. For a copy this client does not own.</summary>
        /// <param name="player">Carries the name this player chose in the lobby.</param>
        /// <param name="health">Decides whether the tag is shown at all.</param>
        /// <param name="avatar">The body's animator; the tag hangs above its head.</param>
        /// <param name="font">The font to draw with; the HUD's own, so names match the rest of the game.</param>
        public void Bind(HeroPlayerNetworkSetup player, PlayerHealthSync health, Animator avatar, TMP_FontAsset font)
        {
            m_player = player;
            m_health = health;

            var head = avatar != null ? avatar.GetBoneTransform(HumanBodyBones.Head) : null;
            var height = head != null ? head.position.y - transform.position.y + HeadClearance : FallbackHeight;

            // Created with its RectTransform up front: TextMeshPro requires one, and adding it to a plain object
            // makes Unity swap the Transform out and destroy it, leaving any reference taken before that dead.
            var holder = new GameObject("NameTag", typeof(RectTransform));
            holder.transform.SetParent(transform, false);
            holder.transform.localPosition = new Vector3(0f, height, 0f);

            m_label = holder.AddComponent<TextMeshPro>();
            m_anchor = m_label.transform;
            m_label.font = font;
            m_label.fontSize = 1.6f;
            m_label.alignment = TextAlignmentOptions.Center;
            m_label.enableWordWrapping = false;
            m_label.rectTransform.sizeDelta = new Vector2(4f, 0.5f);
        }

        private void LateUpdate()
        {
            if (m_label == null || m_anchor == null) return;

            // Following the head bone every frame would bob the name with their walk; it hangs off the body
            // instead, which is steady.
            var camera = LocalPlayerContext.PlayerCamera;
            if (camera == null || (m_health != null && m_health.IsDead))
            {
                m_label.enabled = false;
                return;
            }

            var toCamera = camera.transform.position - m_anchor.position;
            var distance = toCamera.magnitude;
            if (distance > FadeOutRange)
            {
                m_label.enabled = false;
                return;
            }

            m_label.enabled = true;

            if (m_player != null && m_player.DisplayName != m_shownName)
            {
                m_shownName = m_player.DisplayName;
                m_label.text = m_shownName;
            }

            // Square on to the viewer, and readable rather than mirrored.
            m_anchor.rotation = Quaternion.LookRotation(-toCamera, Vector3.up);

            var fade = Mathf.InverseLerp(FadeOutRange, FullyVisibleRange, distance);
            m_label.alpha = Mathf.Clamp01(fade);
        }
    }
}
