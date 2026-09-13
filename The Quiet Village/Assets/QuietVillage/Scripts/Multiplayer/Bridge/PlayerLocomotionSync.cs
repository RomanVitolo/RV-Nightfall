using Unity.Netcode;
using UHFPS.Input;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Publishes the owner's locomotion and lean so remote clients can animate its third-person avatar,
    /// and decides which way that avatar's feet point.
    /// </summary>
    /// <remarks>
    /// Authority: owner. These values are derived from the owner's own <see cref="PlayerStateMachine"/>
    /// and input, and are purely cosmetic elsewhere — no gameplay decision reads them, so there is nothing
    /// for a modified client to gain by lying about them.
    ///
    /// Deliberately not a NetworkAnimator. HEROPLAYER carries ten Animators that drive first-person
    /// arms, a candle and held items; replicating those would spend bandwidth on things no other
    /// player can see. Instead a handful of values drive the third-person controller's blend tree.
    ///
    /// Look itself is not sent here: the camera holder's rotation already replicates through its own
    /// NetworkTransform. Velocity travels in that look's frame — strafe on X, forward on Z — and each
    /// client re-expresses it in the frame its avatar's feet actually face, which differ while the body is
    /// catching up with a turn.
    /// </remarks>
    public class PlayerLocomotionSync : NetworkBehaviour
    {
        // The avatar controller is generated from these same constants by AvatarAnimatorAssets, so a
        // rename here changes both sides at once. They are separate string literals only at the
        // Animator boundary, where a mismatch fails silently: SetFloat on an unknown name just logs,
        // and the remote body stands frozen in its idle pose.
        public const string MoveXParameter = "MoveX";
        public const string MoveZParameter = "MoveZ";
        public const string SpeedParameter = "Speed";
        public const string GroundedParameter = "Grounded";

        [SerializeField] private RemoteAvatarBinder m_avatar;
        [SerializeField] private PlayerStateMachine m_stateMachine;

        [Tooltip("UHFPS camera holder (FPView). Carries the whole look rotation, including yaw.")]
        [SerializeField] private Transform m_lookTransform;

        [Tooltip("Damping applied to replicated velocity so remote avatars do not pop between blend states.")]
        [SerializeField] private float m_speedDamping = 0.1f;

        [Tooltip("Velocity (m/s) and lean (-1..1) are rounded to this step before replication, so sub-step " +
                 "jitter does not mark them dirty and resend them every tick while a player stands still.")]
        [SerializeField] private float m_velocityQuantum = 0.05f;

        [Header("Facing")]
        [Tooltip("While standing still, how far (degrees) the look may turn from the feet before the body " +
                 "turns to follow. Within it only the head and chest turn — see AvatarAim.")]
        [SerializeField] private float m_turnInPlaceAngle = 70f;

        [Tooltip("How fast (degrees/second) the feet turn to catch up with the look.")]
        [SerializeField] private float m_bodyTurnSpeed = 360f;

        [Tooltip("Planar speed (m/s) above which the body keeps facing the look instead of standing its ground.")]
        [SerializeField] private float m_movingThreshold = 0.1f;

        private readonly NetworkVariable<Vector2> m_lookLocalVelocity =
            new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_grounded =
            new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // UHFPS's own lean input, -1 (left) to 1 (right). The input rather than the camera's lean is sent
        // because the camera's value lives inside a protected motion spring; AvatarAim reapplies UHFPS's
        // wall shortening on each client, which is what the camera value would have added.
        private readonly NetworkVariable<float> m_lean =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Crouching changes what the AI can see of a player, so the other clients have to know about it. Sent as
        // the state rather than the pose: head bob rewrites the camera's position every frame, and replicating
        // that would spend a stream of packets to say the same thing.
        private readonly NetworkVariable<bool> m_crouching =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private static readonly int MoveXHash = Animator.StringToHash(MoveXParameter);
        private static readonly int MoveZHash = Animator.StringToHash(MoveZParameter);
        private static readonly int SpeedHash = Animator.StringToHash(SpeedParameter);
        private static readonly int GroundedHash = Animator.StringToHash(GroundedParameter);
        private static readonly int DeadHash = Animator.StringToHash(PlayerActionSync.DeadParameter);

        private float m_bodyYaw;
        private bool m_hasBodyYaw;
        private bool m_catchingUp;

        /// <summary>Replicated planar speed in m/s. Valid on every client.</summary>
        public float PlanarSpeed => m_lookLocalVelocity.Value.magnitude;

        /// <summary>Replicated grounded state. Valid on every client.</summary>
        public bool IsGrounded => m_grounded.Value;

        /// <summary>Replicated lean input, -1 (left) to 1 (right). Valid on every client.</summary>
        public float Lean => m_lean.Value;

        /// <summary>The camera holder, carrying this player's look on every client.</summary>
        public Transform LookTransform => m_lookTransform != null ? m_lookTransform : transform;

        /// <summary>Replicated crouching state. Valid on every client.</summary>
        public bool IsCrouching => m_crouching.Value;

        public override void OnNetworkSpawn()
        {
            if (IsOwner) return;

            // The pose this copy is already in when it arrives, then each change to it.
            m_crouching.OnValueChanged += HandleCrouchingChanged;
            ApplyPose(m_crouching.Value);
        }

        public override void OnNetworkDespawn()
        {
            m_crouching.OnValueChanged -= HandleCrouchingChanged;
        }

        private void HandleCrouchingChanged(bool previous, bool current) => ApplyPose(current);

        /// <summary>
        /// Drops or raises this copy's head, the way UHFPS does for the player it belongs to.
        /// </summary>
        /// <remarks>
        /// The camera holder is where the AI looks for a player's eyes, and it is what a spectator's camera
        /// follows, so a crouching player whose copy stands would be seen over cover they are hiding behind.
        /// The body itself keeps standing: the avatar's animation set has no crouch clips.
        /// </remarks>
        private void ApplyPose(bool crouching)
        {
            if (m_stateMachine == null || m_lookTransform == null) return;

            var state = crouching ? m_stateMachine.CrouchingState : m_stateMachine.StandingState;
            if (state == null) return;

            m_lookTransform.localPosition = m_stateMachine.SetControllerState(state);
        }

        private void Update()
        {
            if (IsOwner) PublishLocalMotion();

            DriveAvatar();
        }

        private void PublishLocalMotion()
        {
            if (m_stateMachine == null) return;

            // Motion is world-space velocity — UHFPS feeds it straight into CharacterController.Move.
            var motion = m_stateMachine.Motion;
            var planar = new Vector3(motion.x, 0f, motion.z);
            var local = Quaternion.Inverse(Quaternion.Euler(0f, LookYaw, 0f)) * planar;

            var velocity = new Vector2(Quantize(local.x), Quantize(local.z));
            if (velocity != m_lookLocalVelocity.Value) m_lookLocalVelocity.Value = velocity;

            var grounded = m_stateMachine.IsGrounded;
            if (grounded != m_grounded.Value) m_grounded.Value = grounded;

            // The same read LeanMotion makes, so the remote body leans exactly when the owner's camera does.
            var lean = Quantize(Mathf.Clamp(InputManager.ReadInput<float>(Controls.LEAN), -1f, 1f));
            if (!Mathf.Approximately(lean, m_lean.Value)) m_lean.Value = lean;

            var crouching = m_stateMachine.StateCrouched;
            if (crouching != m_crouching.Value) m_crouching.Value = crouching;
        }

        private float LookYaw => m_lookTransform != null ? m_lookTransform.eulerAngles.y : transform.eulerAngles.y;

        private float Quantize(float value)
        {
            if (m_velocityQuantum <= 0f) return value;

            return Mathf.Round(value / m_velocityQuantum) * m_velocityQuantum;
        }

        private void DriveAvatar()
        {
            if (m_avatar == null || !m_avatar.IsAvatarActive) return;

            var animator = m_avatar.Animator;
            var avatarRoot = m_avatar.AvatarRoot;
            if (animator == null || avatarRoot == null) return;

            var lookYaw = LookYaw;
            var lookVelocity = m_lookLocalVelocity.Value;

            // A corpse must not swivel with its owner's mouse, so the feet stay where the body fell.
            if (!animator.GetBool(DeadHash)) UpdateBodyYaw(lookYaw, lookVelocity.magnitude);
            avatarRoot.rotation = Quaternion.Euler(0f, m_bodyYaw, 0f);

            // Re-express the look-frame velocity in the frame the feet face. They only differ during a
            // catch-up turn, but without this a strafe during that turn would play the wrong clip.
            var toBody = Quaternion.Euler(0f, lookYaw - m_bodyYaw, 0f);
            var bodyVelocity = toBody * new Vector3(lookVelocity.x, 0f, lookVelocity.y);

            animator.SetFloat(MoveXHash, bodyVelocity.x, m_speedDamping, Time.deltaTime);
            animator.SetFloat(MoveZHash, bodyVelocity.z, m_speedDamping, Time.deltaTime);
            animator.SetFloat(SpeedHash, lookVelocity.magnitude, m_speedDamping, Time.deltaTime);
            animator.SetBool(GroundedHash, m_grounded.Value);
        }

        /// <summary>
        /// Points the feet: planted while the owner only glances around, following once they commit.
        /// </summary>
        /// <remarks>
        /// UHFPS runs its look controller in <c>LookForward</c> mode, which writes yaw *and* pitch onto the
        /// camera holder and never rotates the player root, so the body has no facing of its own to copy.
        /// Snapping it to the look every frame spun the whole body like a turret whenever the owner glanced
        /// sideways. Instead the head and chest take small turns (AvatarAim), and the feet only turn once
        /// the look leaves the turn-in-place angle or the player starts moving — then they catch up fully
        /// rather than stopping at the edge, so the next glance starts from a centred stance.
        /// </remarks>
        private void UpdateBodyYaw(float lookYaw, float speed)
        {
            if (!m_hasBodyYaw)
            {
                m_bodyYaw = lookYaw;
                m_hasBodyYaw = true;
            }

            var moving = speed > m_movingThreshold || !m_grounded.Value;
            if (moving || Mathf.Abs(Mathf.DeltaAngle(m_bodyYaw, lookYaw)) > m_turnInPlaceAngle) m_catchingUp = true;

            if (!m_catchingUp) return;

            m_bodyYaw = Mathf.MoveTowardsAngle(m_bodyYaw, lookYaw, m_bodyTurnSpeed * Time.deltaTime);

            if (!moving && Mathf.Abs(Mathf.DeltaAngle(m_bodyYaw, lookYaw)) < 1f) m_catchingUp = false;
        }
    }
}
