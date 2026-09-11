using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Replicates the avatar's one-off body animations: hit reactions, death, idle fidgets and item actions.
    /// </summary>
    /// <remarks>
    /// Hits and death ride on <see cref="PlayerHealthSync"/>'s server-authoritative health instead of
    /// messages of their own. Every client already receives each health change, so deriving the reaction
    /// from it costs no bandwidth, can never disagree with who actually took damage, and puts a late
    /// joiner in the right pose — it reads a dead player's health as zero on arrival. Variants are picked
    /// from replicated values rather than Random, so every client plays the same clip.
    ///
    /// Fidgets and item actions are owner-originated and purely cosmetic, so the owner decides them.
    /// Fidgets replicate as a counter: an owner-write NetworkVariable keeps authority inside Netcode, and a
    /// late joiner takes the value it finds as a baseline instead of replaying one that finished long ago.
    /// Item actions use an owner-only RPC instead, because each one must play — a counter coalesces to one
    /// value per network tick and would swallow shots fired faster than the tick rate.
    /// </remarks>
    public class PlayerActionSync : NetworkBehaviour
    {
        // Shared with AvatarAnimatorAssets, which builds the controller from these constants.
        public const string DeadParameter = "Dead";
        public const string DeathVariantParameter = "DeathVariant";
        public const string HitParameter = "Hit";
        public const string HitVariantParameter = "HitVariant";
        public const string FidgetParameter = "Fidget";
        public const string IdleVariantParameter = "IdleVariant";
        public const string ShootParameter = "Shoot";
        public const string ReloadParameter = "Reload";
        public const string AttackParameter = "Attack";
        public const string AttackVariantParameter = "AttackVariant";

        /// <summary>Base-layer state a late joiner is placed straight into when the player is already dead.</summary>
        public const string DeadStateName = "Dead";

        // Clips behind each variant blend tree. AvatarAnimatorAssets refuses to build if these disagree.
        public const int DeathVariants = 2;
        public const int HitVariants = 2;
        public const int IdleVariants = 2;
        public const int AttackVariants = 3;

        [SerializeField] private RemoteAvatarBinder m_avatar;
        [SerializeField] private PlayerHealthSync m_health;
        [SerializeField] private PlayerLocomotionSync m_locomotion;

        [Tooltip("Seconds of standing still, picked from this range each time, before the body fidgets.")]
        [SerializeField] private Vector2 m_fidgetDelayRange = new(8f, 16f);

        [Tooltip("Planar speed (m/s) below which the owner counts as standing still.")]
        [SerializeField] private float m_idleSpeedThreshold = 0.1f;

        // Low bit: which idle clip. Upper bits: a sequence number, so the same clip twice in a row still
        // registers as a change. Wrapping is harmless — it only has to differ from the previous value.
        private readonly NetworkVariable<byte> m_fidget =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private static readonly int DeadHash = Animator.StringToHash(DeadParameter);
        private static readonly int DeathVariantHash = Animator.StringToHash(DeathVariantParameter);
        private static readonly int HitHash = Animator.StringToHash(HitParameter);
        private static readonly int HitVariantHash = Animator.StringToHash(HitVariantParameter);
        private static readonly int FidgetHash = Animator.StringToHash(FidgetParameter);
        private static readonly int IdleVariantHash = Animator.StringToHash(IdleVariantParameter);
        private static readonly int ShootHash = Animator.StringToHash(ShootParameter);
        private static readonly int ReloadHash = Animator.StringToHash(ReloadParameter);
        private static readonly int AttackHash = Animator.StringToHash(AttackParameter);
        private static readonly int AttackVariantHash = Animator.StringToHash(AttackVariantParameter);

        private PlayerItemBehaviour[] m_items;
        private float m_stillTime;
        private float m_fidgetDelay;
        private int m_attackCount;

        private bool IsDead => m_health != null && m_health.Health <= 0;

        /// <summary>The avatar's animator while it is being drawn; <c>null</c> for the owner's hidden body.</summary>
        private Animator ActiveAnimator =>
            m_avatar != null && m_avatar.IsAvatarActive ? m_avatar.Animator : null;

        public override void OnNetworkSpawn()
        {
            if (m_health != null)
            {
                m_health.HealthChanged += HandleHealthChanged;

                // Initial state arrives as a value, not a change, so a player who died before this client
                // joined would otherwise stand up. Snap to the end so the fall is not replayed either.
                if (m_health.Health <= 0) SetDead(true, snapToEnd: true);
            }
            else
            {
                Debug.LogError($"{nameof(PlayerActionSync)}: no PlayerHealthSync assigned; hits and death will not animate.", this);
            }

            m_fidget.OnValueChanged += HandleFidgetChanged;
            m_fidgetDelay = NextFidgetDelay();

            // Items tick themselves, but every one returns early unless equipped, and only PlayerItemsManager
            // equips them — a PlayerComponent HeroPlayerNetworkSetup disables on remote copies. So only the
            // owner's items ever act. They are fixed children of the prefab, so one scan suffices.
            if (IsOwner) SubscribeToItems();
        }

        public override void OnNetworkDespawn()
        {
            if (m_health != null) m_health.HealthChanged -= HandleHealthChanged;

            m_fidget.OnValueChanged -= HandleFidgetChanged;
            UnsubscribeFromItems();
        }

        private void Update()
        {
            if (IsOwner) TickFidget();
        }

        // ---- Item actions ----------------------------------------------------------------------------

        private void SubscribeToItems()
        {
            m_items = GetComponentsInChildren<PlayerItemBehaviour>(true);

            foreach (var item in m_items)
            {
                if (item != null) item.ActionPerformed += HandleItemAction;
            }
        }

        private void UnsubscribeFromItems()
        {
            if (m_items == null) return;

            foreach (var item in m_items)
            {
                if (item != null) item.ActionPerformed -= HandleItemAction;
            }

            m_items = null;
        }

        private void HandleItemAction(PlayerItemBehaviour.ItemAction action)
        {
            if (!IsSpawned) return;

            // Cycled rather than random, so consecutive swings visibly differ.
            var variant = action == PlayerItemBehaviour.ItemAction.Attack ? m_attackCount++ % AttackVariants : 0;
            PlayItemActionRpc(action, (byte)variant);
        }

        /// <summary>Plays one item action on this player's body for everyone else.</summary>
        /// <remarks>
        /// Owner-only invocation is enforced by Netcode, so no client can make another player appear to
        /// fire. Not sent to the owner, whose body is hidden and whose first-person item already animates.
        /// </remarks>
        [Rpc(SendTo.NotOwner, InvokePermission = RpcInvokePermission.Owner)]
        private void PlayItemActionRpc(PlayerItemBehaviour.ItemAction action, byte variant)
        {
            var animator = ActiveAnimator;
            if (animator == null || IsDead) return;

            switch (action)
            {
                case PlayerItemBehaviour.ItemAction.Shoot:
                    animator.SetTrigger(ShootHash);
                    break;

                case PlayerItemBehaviour.ItemAction.Reload:
                    animator.SetTrigger(ReloadHash);
                    break;

                case PlayerItemBehaviour.ItemAction.Attack:
                    // Clamped: the value arrives from another client, and an out-of-range variant would
                    // blend two attack clips together instead of selecting one.
                    animator.SetFloat(AttackVariantHash, Mathf.Min(variant, AttackVariants - 1));
                    animator.SetTrigger(AttackHash);
                    break;
            }
        }

        // ---- Fidgets ---------------------------------------------------------------------------------

        private void TickFidget()
        {
            if (m_locomotion == null) return;

            var standingStill = m_locomotion.IsGrounded
                                && m_locomotion.PlanarSpeed < m_idleSpeedThreshold
                                && !IsDead;

            if (!standingStill)
            {
                m_stillTime = 0f;
                return;
            }

            m_stillTime += Time.deltaTime;
            if (m_stillTime < m_fidgetDelay) return;

            m_stillTime = 0f;
            m_fidgetDelay = NextFidgetDelay();

            // Owner-local randomness is fine: the result is replicated, never re-rolled on other clients.
            var variant = Random.Range(0, IdleVariants);
            var sequence = (m_fidget.Value >> 1) + 1;
            m_fidget.Value = (byte)(((sequence << 1) | variant) & 0xFF);
        }

        private float NextFidgetDelay()
        {
            var min = Mathf.Max(0.5f, Mathf.Min(m_fidgetDelayRange.x, m_fidgetDelayRange.y));
            var max = Mathf.Max(min, Mathf.Max(m_fidgetDelayRange.x, m_fidgetDelayRange.y));
            return Random.Range(min, max);
        }

        private void HandleFidgetChanged(byte previous, byte current)
        {
            var animator = ActiveAnimator;
            if (animator == null) return;

            animator.SetFloat(IdleVariantHash, current & 1);
            animator.SetTrigger(FidgetHash);
        }

        // ---- Health ----------------------------------------------------------------------------------

        private void HandleHealthChanged(int previous, int current)
        {
            if (current <= 0 && previous > 0)
            {
                SetDead(true, snapToEnd: false);
                return;
            }

            if (current > 0 && previous <= 0)
            {
                SetDead(false, snapToEnd: false);
                return;
            }

            if (current >= previous) return;

            var animator = ActiveAnimator;
            if (animator == null) return;

            // Remaining health is identical on every client, which makes it a free shared random seed.
            animator.SetFloat(HitVariantHash, current % HitVariants);
            animator.SetTrigger(HitHash);
        }

        private void SetDead(bool dead, bool snapToEnd)
        {
            var animator = ActiveAnimator;
            if (animator == null) return;

            // Keyed to the player rather than the killing blow, so one player always dies the same way on
            // every screen, including for clients that were not connected when it happened.
            animator.SetFloat(DeathVariantHash, (float)(OwnerClientId % DeathVariants));
            animator.SetBool(DeadHash, dead);

            if (dead && snapToEnd) animator.Play(DeadStateName, 0, 1f);
        }
    }
}
