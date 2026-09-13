using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Poses a remote player's upper body to show where its owner is aiming: head and chest follow the
    /// look, and the spine leans when its owner peeks around a corner.
    /// </summary>
    /// <remarks>
    /// Lives on the avatar's Animator object because Unity only calls <c>OnAnimatorIK</c> there.
    ///
    /// Look needs no replication of its own: the camera holder's rotation already arrives through its
    /// NetworkTransform. <see cref="PlayerLocomotionSync"/> decides where the feet point; within its
    /// turn-in-place angle only the head and chest turn, which is what makes glancing sideways read as
    /// glancing rather than as the whole body spinning. Built-in humanoid look-at rather than the pack's
    /// <c>Rotate_90_*</c> aim poses, which are authored around the Defender's rifle.
    ///
    /// Lean arrives as the owner's replicated lean input. UHFPS leans the camera sideways and shortens the
    /// lean against walls; this rolls the spine by the angle that moves the head the same distance, and
    /// runs the same wall probe against this client's copy of the level, so the body stops at the wall on
    /// every screen without the owner having to send the reduced value. The distance, probe radius and
    /// mask are copied from UHFPS's LeanMotion by the setup tool, so the two cannot drift apart.
    ///
    /// The lean edits bones after the Animator has written them, which is only safe because the setup tool
    /// sets the avatar's Animator to Always Animate: with culling, an off-screen Animator skips writing the
    /// pose and the edit would compound every frame.
    /// </remarks>
    [RequireComponent(typeof(Animator))]
    public class AvatarAim : MonoBehaviour
    {
        [SerializeField] private Animator m_animator;

        [Tooltip("UHFPS camera holder (FPView), whose forward is the owner's replicated look direction.")]
        [SerializeField] private Transform m_lookTransform;

        [Tooltip("Source of the replicated lean input.")]
        [SerializeField] private PlayerLocomotionSync m_locomotion;

        [Header("Look")]
        [Tooltip("How much of the look the chest takes. Higher reads as aiming; too high bends the spine.")]
        [SerializeField, Range(0f, 1f)] private float m_bodyWeight = 0.3f;

        [SerializeField, Range(0f, 1f)] private float m_headWeight = 1f;

        [Tooltip("0 lets the head turn freely; 1 locks it. 0.5 allows about 90 degrees, which covers the " +
                 "turn-in-place angle before the feet follow.")]
        [SerializeField, Range(0f, 1f)] private float m_clampWeight = 0.5f;

        [Tooltip("Rate (per second) at which the aim pose fades in and out, e.g. on death.")]
        [SerializeField] private float m_blendSpeed = 4f;

        [Header("Lean (copied from UHFPS LeanMotion by the setup tool)")]
        [Tooltip("How far sideways (m) a full lean moves the head.")]
        [SerializeField] private float m_leanDistance = 0.3f;

        [Tooltip("Radius (m) of the wall probe that shortens a lean.")]
        [SerializeField] private float m_leanProbeRadius = 0.2f;

        [SerializeField] private LayerMask m_leanMask = 1;

        [Tooltip("How quickly the body follows the lean input. UHFPS eases the camera with a spring; this " +
                 "keeps the body in step with it.")]
        [SerializeField] private float m_leanSmoothTime = 0.12f;

        private const float LookDistance = 10f;

        private static readonly int DeadHash = Animator.StringToHash(PlayerActionSync.DeadParameter);

        private readonly Transform[] m_spine = new Transform[3];
        private int m_spineCount;
        private float m_spineHeight;
        private Transform m_head;

        private float m_weight;
        private float m_lean;
        private float m_leanVelocity;

        private void Awake()
        {
            if (m_animator == null) m_animator = GetComponent<Animator>();
            CacheBones();
        }

        /// <summary>Takes another body's wiring and tuning, for a body that replaces it at runtime.</summary>
        /// <param name="animator">This body's own Animator.</param>
        public void CopySettingsFrom(AvatarAim source, Animator animator)
        {
            if (source == null) return;

            m_animator = animator != null ? animator : GetComponent<Animator>();
            m_lookTransform = source.m_lookTransform;
            m_locomotion = source.m_locomotion;

            m_bodyWeight = source.m_bodyWeight;
            m_headWeight = source.m_headWeight;
            m_clampWeight = source.m_clampWeight;
            m_blendSpeed = source.m_blendSpeed;

            m_leanDistance = source.m_leanDistance;
            m_leanProbeRadius = source.m_leanProbeRadius;
            m_leanMask = source.m_leanMask;
            m_leanSmoothTime = source.m_leanSmoothTime;

            // Awake has already cached bones if this object was active; a different rig needs them again.
            CacheBones();
        }

        private void CacheBones()
        {
            m_spineCount = 0;
            if (m_animator == null || !m_animator.isHuman) return;

            // UpperChest is optional in Unity's humanoid map, so the lean spreads over whichever exist.
            foreach (var bone in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            {
                var boneTransform = m_animator.GetBoneTransform(bone);
                if (boneTransform != null) m_spine[m_spineCount++] = boneTransform;
            }

            m_head = m_animator.GetBoneTransform(HumanBodyBones.Head);

            // The lever arm of the lean: rolling the spine by an angle moves the head by about
            // height * sin(angle), which is how a lean distance becomes a roll angle.
            m_spineHeight = m_spineCount > 0 && m_head != null
                ? Vector3.Distance(m_spine[0].position, m_head.position)
                : 0f;
        }

        private bool IsDead => m_animator != null && m_animator.GetBool(DeadHash);

        private void OnAnimatorIK(int layerIndex)
        {
            if (layerIndex != 0 || m_animator == null || m_lookTransform == null || m_head == null) return;

            // A corpse that keeps tracking its owner's mouse reads as alive.
            m_weight = Mathf.MoveTowards(m_weight, IsDead ? 0f : 1f, m_blendSpeed * Time.deltaTime);

            m_animator.SetLookAtWeight(m_weight, m_bodyWeight, m_headWeight, 0f, m_clampWeight);
            m_animator.SetLookAtPosition(m_head.position + m_lookTransform.forward * LookDistance);
        }

        private void LateUpdate()
        {
            if (m_locomotion == null || m_spineCount == 0 || m_spineHeight <= 0f) return;
            if (m_animator == null || !m_animator.isActiveAndEnabled) return;

            var target = IsDead ? 0f : Mathf.Clamp(m_locomotion.Lean, -1f, 1f);
            m_lean = Mathf.SmoothDamp(m_lean, target, ref m_leanVelocity, m_leanSmoothTime);

            if (Mathf.Abs(m_lean) < 0.001f) return;

            var offset = ShortenAgainstWalls(m_lean * m_leanDistance);
            var rollDegrees = Mathf.Asin(Mathf.Clamp(offset / m_spineHeight, -1f, 1f)) * Mathf.Rad2Deg;

            // Rolling about the body's forward axis by a negative angle tips the head toward its right, the
            // same sign convention UHFPS uses for the camera tilt. Each bone takes a share, and the shares
            // compound down the chain into the full roll at the top of the spine.
            var axis = transform.forward;
            var share = -rollDegrees / m_spineCount;

            for (var i = 0; i < m_spineCount; i++)
            {
                m_spine[i].rotation = Quaternion.AngleAxis(share, axis) * m_spine[i].rotation;
            }
        }

        /// <summary>The same wall check UHFPS runs on the owner's camera, run against this client's level.</summary>
        /// <param name="offset">Signed sideways head offset in metres; positive is to the body's right.</param>
        private float ShortenAgainstWalls(float offset)
        {
            var distance = Mathf.Abs(offset);
            if (distance <= 0f || m_head == null) return offset;

            var direction = transform.right * Mathf.Sign(offset);
            if (Physics.SphereCast(m_head.position, m_leanProbeRadius, direction, out var hit, distance,
                    m_leanMask, QueryTriggerInteraction.Ignore))
            {
                return Mathf.Sign(offset) * hit.distance;
            }

            return offset;
        }
    }
}
