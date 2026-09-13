using QuietVillage.Multiplayer.Characters;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
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

        /// <summary>
        /// Swaps the authored body for a character's own, keeping its place, layer, animation and aim settings.
        /// </summary>
        /// <remarks>
        /// The prefab nests one body, the Worker, fully wired by the setup tool. Every other character is built from its
        /// vendor prefab at spawn rather than nested too: a nested body per character would put a dozen skinned meshes
        /// on every player, four times over. Call before anything binds to <see cref="Animator"/>, since the old one
        /// is destroyed.
        /// </remarks>
        /// <returns><c>false</c> if the body was kept, e.g. the character has no prefab.</returns>
        public bool ReplaceBody(CharacterCatalog.Character character, int variantIndex, RuntimeAnimatorController controller)
        {
            var variant = character?.VariantAt(variantIndex);
            if (variant == null || variant.Prefab == null || m_avatarRoot == null) return false;

            var previous = m_avatarRoot;
            var previousAnimator = m_animator;
            var shared = controller != null ? controller
                : previousAnimator != null ? previousAnimator.runtimeAnimatorController
                : null;

            var body = CharacterBodies.Create(variant.Prefab, previous.transform.parent, character.HumanoidAvatar,
                shared, character.BodyScale);
            if (body == null) return false;

            body.name = previous.name;
            body.transform.SetLocalPositionAndRotation(previous.transform.localPosition, previous.transform.localRotation);
            CharacterBodies.SetLayer(body, previous.layer);

            // AvatarAim has to live on the Animator's own object, and carries tuning copied from UHFPS by the setup tool.
            var previousAim = previous.GetComponent<AvatarAim>();
            if (previousAim != null) body.AddComponent<AvatarAim>().CopySettingsFrom(previousAim, body.GetComponent<Animator>());

            body.SetActive(previous.activeSelf);

            // Deactivated first: Destroy waits for the end of the frame, and two bodies would draw until then.
            previous.SetActive(false);
            Destroy(previous);

            m_avatarRoot = body;
            m_animator = body.GetComponent<Animator>();
            return true;
        }

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
