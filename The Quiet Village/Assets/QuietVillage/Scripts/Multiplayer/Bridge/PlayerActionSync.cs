using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
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

        // Owner-written: which item this player holds, and whether its light is on. Both are cosmetic elsewhere
        // — they decide what the body carries and whether a beam shines, never an outcome — so the owner decides.
        private readonly NetworkVariable<sbyte> m_equippedItem =
            new(NoItem, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_heldLightOn =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>Nothing equipped, matching PlayerItemsManager's own "no current item".</summary>
        private const sbyte NoItem = -1;

        private PlayerItemsManager m_itemsManager;
        private PlayerItemBehaviour m_lastLightItem;
        private Light m_lastItemLight;

        /// <summary>Raised on every client but the owner's, when this player's item acts.</summary>
        /// <remarks>The body animates from the same message; this lets a listener add the sound it makes.</remarks>
        public event System.Action<PlayerItemBehaviour.ItemAction> ItemActionPlayed;

        /// <summary>Raised on every client but the owner's, with a sound this player's item animation played.</summary>
        public event System.Action<SoundClip> ItemSoundPlayed;

        /// <summary>Raised on the other clients when this player puts one item away and takes another out.</summary>
        public event System.Action<PlayerItemBehaviour, PlayerItemBehaviour> EquippedItemChanged;

        /// <summary>Raised on the other clients when this player's held light is switched on or off.</summary>
        public event System.Action<bool> HeldLightToggled;

        private AnimationSoundEvent[] m_itemSounds;

        /// <summary>The item this player holds, resolved on any client, or <c>null</c> if their hands are empty.</summary>
        /// <remarks>
        /// The index is into PlayerItemsManager's own list, which is the same fixed list of prefab children on
        /// every copy of the player, so the index means the same thing everywhere.
        /// </remarks>
        public PlayerItemBehaviour EquippedItem => ItemAt(m_equippedItem.Value);

        /// <summary>Which of PlayerItemsManager's items this player holds, or -1 for empty hands.</summary>
        public int EquippedItemIndex => m_equippedItem.Value;

        /// <summary>Whether the held item's light is currently on, e.g. a lit flashlight.</summary>
        public bool HeldLightOn => m_heldLightOn.Value;

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
            // Needed on every copy: the owner publishes the index, the others resolve it back to an item.
            m_itemsManager = GetComponentInChildren<PlayerItemsManager>(true);

            // Fixed children of the prefab, so the same scan gives the same order on every copy, and an index
            // into it means the same component everywhere.
            m_itemSounds = GetComponentsInChildren<AnimationSoundEvent>(true);

            if (IsOwner)
            {
                SubscribeToItems();
                SubscribeToItemSounds();
            }
            else
            {
                m_equippedItem.OnValueChanged += HandleEquippedItemChanged;
                m_heldLightOn.OnValueChanged += HandleHeldLightChanged;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (m_health != null) m_health.HealthChanged -= HandleHealthChanged;

            m_fidget.OnValueChanged -= HandleFidgetChanged;
            m_equippedItem.OnValueChanged -= HandleEquippedItemChanged;
            m_heldLightOn.OnValueChanged -= HandleHeldLightChanged;

            UnsubscribeFromItems();
            UnsubscribeFromItemSounds();
        }

        private void Update()
        {
            if (!IsOwner) return;

            TickFidget();
            TickEquippedItem();
        }

        // ---- Held item -------------------------------------------------------------------------------

        /// <summary>Publishes what the owner holds, so the other clients can light and sound it.</summary>
        private void TickEquippedItem()
        {
            if (m_itemsManager == null) return;

            var current = m_itemsManager.CurrentItem;
            var index = current != null ? (sbyte)m_itemsManager.CurrentItemIndex : NoItem;
            if (index != m_equippedItem.Value) m_equippedItem.Value = index;

            var lightOn = IsLightOn(current);
            if (lightOn != m_heldLightOn.Value) m_heldLightOn.Value = lightOn;
        }

        private bool IsLightOn(PlayerItemBehaviour item)
        {
            if (item == null) return false;

            // Cached per item: this runs every frame, and an item's light never moves between its children.
            if (!ReferenceEquals(item, m_lastLightItem))
            {
                m_lastLightItem = item;
                m_lastItemLight = item.GetComponentInChildren<Light>(true);
            }

            return m_lastItemLight != null && m_lastItemLight.enabled && m_lastItemLight.gameObject.activeInHierarchy;
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
            if (IsDead) return;

            ItemActionPlayed?.Invoke(action);

            var animator = ActiveAnimator;
            if (animator == null) return;

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

        // ---- Item sounds -----------------------------------------------------------------------------

        private void SubscribeToItemSounds()
        {
            for (var i = 0; i < m_itemSounds.Length; i++)
            {
                if (m_itemSounds[i] == null) continue;

                // The index is captured per subscription: the receiver needs to know which item spoke.
                var index = (byte)i;
                m_itemSounds[i].SoundPlayed += name => HandleItemSound(index, name);
            }
        }

        private void UnsubscribeFromItemSounds()
        {
            // The closures above are per subscription and cannot be removed by name; the components die with the
            // player object they are on, which is the same object this despawns with.
            m_itemSounds = null;
        }

        private void HandleItemSound(byte index, string soundName)
        {
            if (IsSpawned) PlayItemSoundRpc(index, soundName);
        }

        /// <summary>Plays a sound this player's item animation made, on their body, for everyone else.</summary>
        /// <remarks>
        /// The name is sent rather than the clip: clips cannot travel, and every client has the same item with the
        /// same named sounds on its own copy of this player.
        /// </remarks>
        [Rpc(SendTo.NotOwner, InvokePermission = RpcInvokePermission.Owner)]
        private void PlayItemSoundRpc(byte index, string soundName)
        {
            if (IsDead || m_itemSounds == null || index >= m_itemSounds.Length) return;

            var source = m_itemSounds[index];
            if (source == null) return;

            var sound = source.GetSound(soundName);
            if (sound != null) ItemSoundPlayed?.Invoke(sound);
        }

        private void HandleEquippedItemChanged(sbyte previous, sbyte current) =>
            EquippedItemChanged?.Invoke(ItemAt(previous), ItemAt(current));

        private void HandleHeldLightChanged(bool previous, bool current) => HeldLightToggled?.Invoke(current);

        private PlayerItemBehaviour ItemAt(sbyte index)
        {
            var items = m_itemsManager != null ? m_itemsManager.PlayerItems : null;
            return items != null && index >= 0 && index < items.Count ? items[index] : null;
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
