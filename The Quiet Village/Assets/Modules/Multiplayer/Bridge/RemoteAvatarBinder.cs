using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Carries the third-person avatar that represents this player on other clients.
    /// </summary>
    /// <remarks>
    /// UHFPS ships first-person hands only (<c>Hands.fbx</c>), so a player has no body that anyone
    /// else could see. This component holds a separate humanoid rig which is shown to every client
    /// except the owner — the owner's camera sits inside it and would otherwise be looking at the
    /// inside of their own head.
    /// </remarks>
    public class RemoteAvatarBinder : MonoBehaviour
    {
        [Tooltip("Root of the third-person avatar. Hidden for the owning client.")]
        [SerializeField] private GameObject m_avatarRoot;

        [Tooltip("Animator on the avatar, driven by replicated locomotion state.")]
        [SerializeField] private Animator m_animator;

        /// <summary>The avatar's animator, or <c>null</c> if the prefab was not wired up.</summary>
        public Animator Animator => m_animator;

        /// <summary>The avatar's root transform, or <c>null</c> if the prefab was not wired up.</summary>
        public Transform AvatarRoot => m_avatarRoot != null ? m_avatarRoot.transform : null;

        /// <summary>True once the avatar is present and actually being rendered.</summary>
        public bool IsAvatarActive => m_avatarRoot != null && m_avatarRoot.activeInHierarchy;

        /// <summary>Shows the avatar to remote clients, hides it for the owner.</summary>
        public void SetAvatarVisible(bool visible)
        {
            if (m_avatarRoot == null)
            {
                Debug.LogError($"{nameof(RemoteAvatarBinder)}: no avatar root assigned, so this player is invisible to others.", this);
                return;
            }

            // Local-only state: each client decides independently whether to draw this player's body.
            m_avatarRoot.SetActive(visible);
        }
    }
}
