using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Publishes the owner's locomotion so remote clients can animate its third-person avatar.
    /// </summary>
    /// <remarks>
    /// Authority: owner. These values are derived from the owner's own <see cref="PlayerStateMachine"/>
    /// motion and are purely cosmetic elsewhere — no gameplay decision reads them, so there is nothing
    /// for a modified client to gain by lying about them.
    ///
    /// Deliberately not a NetworkAnimator. HEROPLAYER carries ten Animators that drive first-person
    /// arms, a candle and held items; replicating those would spend bandwidth on things no other
    /// player can see. Instead a handful of scalars drive the third-person controller's blend tree.
    /// </remarks>
    public class PlayerLocomotionSync : NetworkBehaviour
    {
        [SerializeField] private RemoteAvatarBinder m_avatar;
        [SerializeField] private PlayerStateMachine m_stateMachine;

        [Tooltip("UHFPS camera holder (FPView). Carries the whole look rotation, including yaw.")]
        [SerializeField] private Transform m_lookTransform;

        [Tooltip("Damping applied to the replicated speed so remote avatars do not pop between blend states.")]
        [SerializeField] private float m_speedDamping = 0.1f;

        [Tooltip("Planar speed below which the avatar is considered idle.")]
        [SerializeField] private float m_movingThreshold = 0.1f;

        private readonly NetworkVariable<float> m_speed =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_grounded =
            new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_jumping =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_freeFall =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Parameters of StarterAssetsThirdPerson.controller, verified against the asset.
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");
        private static readonly int GroundedHash = Animator.StringToHash("Grounded");
        private static readonly int JumpHash = Animator.StringToHash("Jump");
        private static readonly int FreeFallHash = Animator.StringToHash("FreeFall");

        private void Update()
        {
            if (IsOwner) PublishLocalMotion();

            DriveAvatar();
        }

        private void PublishLocalMotion()
        {
            if (m_stateMachine == null) return;

            var motion = m_stateMachine.Motion;
            var grounded = m_stateMachine.IsGrounded;

            // Vertical speed is excluded so falling does not read as running.
            m_speed.Value = new Vector3(motion.x, 0f, motion.z).magnitude;
            m_grounded.Value = grounded;

            // The controller expects jump and free fall as sustained bools rather than one-shot
            // triggers, so a dropped trigger cannot leave a remote avatar stuck mid-air.
            m_jumping.Value = !grounded && motion.y > 0f;
            m_freeFall.Value = !grounded && motion.y <= 0f;
        }

        /// <summary>
        /// Turns the third-person body to match where the player is looking.
        /// </summary>
        /// <remarks>
        /// UHFPS runs its look controller in <c>LookForward</c> mode, which writes yaw *and* pitch onto
        /// the camera holder and never rotates the player root. The avatar is parented to that root, so
        /// without this it would face a fixed direction no matter where its owner turned. Pitch is
        /// dropped — leaning the whole body back to look up would be worse than not aiming the head.
        /// </remarks>
        private void FaceAvatarAlongLook()
        {
            if (m_lookTransform == null) return;

            var avatarRoot = m_avatar.AvatarRoot;
            if (avatarRoot == null) return;

            avatarRoot.rotation = Quaternion.Euler(0f, m_lookTransform.eulerAngles.y, 0f);
        }

        private void DriveAvatar()
        {
            if (m_avatar == null || !m_avatar.IsAvatarActive) return;

            var animator = m_avatar.Animator;
            if (animator == null) return;

            FaceAvatarAlongLook();

            animator.SetFloat(SpeedHash, m_speed.Value, m_speedDamping, Time.deltaTime);
            animator.SetFloat(MotionSpeedHash, m_speed.Value > m_movingThreshold ? 1f : 0f);
            animator.SetBool(GroundedHash, m_grounded.Value);
            animator.SetBool(JumpHash, m_jumping.Value);
            animator.SetBool(FreeFallHash, m_freeFall.Value);
        }
    }
}
