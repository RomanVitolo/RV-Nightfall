using System;
using System.Collections.Generic;
using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Replicates player health, with the server as the sole authority over the value, including whether a player is
    /// down and waiting for a teammate or dead.
    /// </summary>
    /// <remarks>
    /// Authority: server. Health is the one player value a client must never be able to declare for
    /// itself — otherwise any modified client is immortal, and two clients can disagree about who
    /// died. The owner <em>requests</em> damage and healing it detects on itself (medkits, damage zones,
    /// falls) through <see cref="PlayerHealth.Authority"/>; AI attacks already run on the server. Only the
    /// server writes the result.
    ///
    /// The owner mirrors the replicated value into its local <see cref="PlayerHealth"/> through
    /// <see cref="PlayerHealth.SetAuthoritativeHealth"/> rather than calling <c>ApplyDamage</c>, which would
    /// only send another request. It raises UHFPS's health-changed and death callbacks and plays the blood and
    /// hurt-sound feedback, so the existing UI and death flow keep working without the damage being applied twice.
    ///
    /// Downed: a blow that would kill a player puts them down instead, for <see cref="m_bleedOutSeconds"/>, while a
    /// teammate can still stand. A standing teammate holding Use on them revives them (<see cref="RequestRevive"/>).
    /// Any further harm, the bleed-out running out, or nobody left standing to help makes it death. Down and dead
    /// travel inside the health value itself (0 is down, -1 is dead) rather than as a second variable: a client
    /// reading a health of zero before a separate "downed" flag arrived would run UHFPS's death for a player who is
    /// only down, and that death cannot be taken back.
    ///
    /// The owner mirrors a downed player's health to UHFPS as 1, so UHFPS never runs its death until the real one.
    /// </remarks>
    public class PlayerHealthSync : NetworkBehaviour, IPlayerHealthAuthority
    {
        [SerializeField] private PlayerHealth m_playerHealth;

        [Header("Downed")]
        [Tooltip("Whether a killing blow puts a player down for a teammate to revive. Off makes it death at once.")]
        [SerializeField] private bool m_downedEnabled = true;

        [Tooltip("Seconds a downed player lasts before they die.")]
        [SerializeField] private float m_bleedOutSeconds = 30f;

        [Tooltip("Health a revived player gets back up with.")]
        [SerializeField] private int m_reviveHealth = 30;

        [Tooltip("How far (m) a reviver may stand from the downed player, checked by the host.")]
        [SerializeField] private float m_reviveReach = 3.5f;

        [Tooltip("Health a player returning mid-level (e.g. at dawn after dying) starts with.")]
        [SerializeField] private int m_returnHealth = 40;

        /// <summary>The replicated value for a player who is down.</summary>
        private const int DownedValue = 0;

        /// <summary>The replicated value for a player who is dead.</summary>
        private const int DeadValue = -1;

        private readonly NetworkVariable<int> m_health =
            new(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server time a downed player dies at. Read by every client for the countdown only; the host decides.
        private readonly NetworkVariable<double> m_bleedOutAt =
            new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private int m_maxHealth = 100;

        private static readonly List<PlayerHealthSync> s_spawned = new();

        /// <summary>Every player spawned in the level, on every client, whoever owns them.</summary>
        public static IReadOnlyList<PlayerHealthSync> Spawned => s_spawned;

        /// <summary>Server: a player has gone down.</summary>
        public static event Action<PlayerHealthSync> ServerDowned;

        /// <summary>Server: a downed player was revived; the second is who revived them, or <c>null</c> (e.g. at dawn).</summary>
        public static event Action<PlayerHealthSync, PlayerHealthSync> ServerRevived;

        /// <summary>Server: a player has died.</summary>
        public static event Action<PlayerHealthSync> ServerDied;

        /// <summary>True once at least one player has spawned and every connected player is dead.</summary>
        /// <remarks>
        /// A player who left is despawned and disconnected, so they neither keep the others alive nor count as dead.
        /// A downed player is not dead yet: they die on their own once nobody is left standing.
        ///
        /// A player still loading the level is connected but not yet spawned, and counts as alive. Counting only spawned
        /// players let the host die in that window and be offered Restart Level over the head of someone about to
        /// arrive. Every client knows who is connected: the server tells them as clients join and leave.
        /// </remarks>
        public static bool EveryoneDead
        {
            get
            {
                var anyone = false;
                foreach (var player in s_spawned)
                {
                    if (player == null) continue;
                    if (!player.IsDead) return false;

                    anyone = true;
                }

                return anyone && !AnyoneStillArriving();
            }
        }

        private static bool AnyoneStillArriving()
        {
            var networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening) return false;

            foreach (var clientId in networkManager.ConnectedClientsIds)
            {
                var spawned = false;
                foreach (var player in s_spawned)
                {
                    if (player == null || player.OwnerClientId != clientId) continue;

                    spawned = true;
                    break;
                }

                if (!spawned) return true;
            }

            return false;
        }

        /// <summary>Current health, zero while down or dead. Readable on every client, written only by the server.</summary>
        public int Health => Mathf.Max(0, m_health.Value);

        /// <summary>Dead by the replicated health. Death is permanent for this player object.</summary>
        public bool IsDead => m_health.Value <= DeadValue;

        /// <summary>Down and waiting for a teammate: not dead, but not able to act either.</summary>
        public bool IsDowned => m_health.Value == DownedValue;

        /// <summary>Alive and on their feet.</summary>
        public bool IsStanding => m_health.Value > DownedValue;

        /// <summary>Seconds a downed player has left, as this client estimates server time. Zero unless downed.</summary>
        public float BleedOutRemaining => IsDowned && IsSpawned
            ? Mathf.Max(0f, (float)(m_bleedOutAt.Value - NetworkManager.ServerTime.Time))
            : 0f;

        /// <summary>Raised on every client when this player's health changes, as <see cref="Health"/> values.</summary>
        public event Action<int, int> HealthChanged;

        public override void OnNetworkSpawn()
        {
            if (m_playerHealth != null)
            {
                m_maxHealth = (int)m_playerHealth.MaxHealth;

                // Set on every copy, not only the owner's: a remote copy has to swallow what it detects rather than
                // change its own health locally, where nothing would ever correct it.
                m_playerHealth.Authority = this;
            }

            if (IsServer)
            {
                var start = m_playerHealth != null ? (int)m_playerHealth.StartHealth : m_maxHealth;

                var character = GetComponent<Characters.PlayerCharacter>();
                if (character != null && character.Returning)
                {
                    // Back from the dead mid-level: weak, and never the health a resumed save began them with.
                    start = m_returnHealth;
                }
                else
                {
                    // A resumed game starts each player at the health they were saved with. The client's account reached
                    // the host before this spawn, which the save's spawn gate waits for. At least 1: a save made after
                    // someone died must not resume them as a corpse with nothing to spectate.
                    var world = FindAnyObjectByType<World.WorldSync>();
                    if (world != null && world.TryGetSavedHealth(OwnerClientId, out var saved)) start = Mathf.Max(1, saved);
                }

                m_health.Value = Mathf.Clamp(start, 1, m_maxHealth);
            }

            m_health.OnValueChanged += HandleHealthChanged;
            s_spawned.Add(this);

            // A late joiner receives the current value as initial state rather than as a change, so
            // apply it once on spawn or its UI would sit at the prefab default.
            if (IsOwner) MirrorToLocalHealth(m_health.Value, false);
        }

        public override void OnNetworkDespawn()
        {
            m_health.OnValueChanged -= HandleHealthChanged;
            s_spawned.Remove(this);

            if (m_playerHealth != null && ReferenceEquals(m_playerHealth.Authority, this))
                m_playerHealth.Authority = null;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || !IsDowned) return;

            // Nobody left on their feet means nobody is coming: a room of downed players would otherwise wait out
            // every bleed-out in silence before the run could end.
            if (NetworkManager.ServerTime.Time >= m_bleedOutAt.Value || !AnyoneElseStanding()) Kill();
        }

        void IPlayerHealthAuthority.RequestDamage(int damage)
        {
            // Only the owner reports damage to itself. A damage zone or a fall is noticed by the client moving the
            // body; a remote copy noticing the same event would count it twice. That also leaves friendly fire off.
            if (IsOwner) RequestDamageRpc(damage);
        }

        void IPlayerHealthAuthority.RequestHeal(int healAmount)
        {
            if (IsOwner) RequestHealRpc(healAmount);
        }

        /// <summary>Asks the server to damage this player.</summary>
        /// <param name="damage">Damage amount; non-positive values are ignored.</param>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestDamageRpc(int damage) => ApplyServerDamage(damage);

        /// <summary>
        /// Damages this player from code already running on the server, such as an AI attack.
        /// </summary>
        /// <remarks>
        /// Server-side callers write the value directly instead of sending themselves an RPC. Ignored anywhere
        /// else, so an AI whose attack animation fires on a client cannot deal damage from there.
        ///
        /// A blow that would kill puts the player down; any blow to a downed player kills them.
        /// </remarks>
        public void ApplyServerDamage(int damage)
        {
            if (!IsServer || damage <= 0 || IsDead) return;

            if (IsDowned)
            {
                Kill();
                return;
            }

            var remaining = m_health.Value - damage;
            if (remaining > 0)
            {
                m_health.Value = Mathf.Min(remaining, m_maxHealth);
                return;
            }

            if (m_downedEnabled && AnyoneElseStanding()) GoDown();
            else Kill();
        }

        /// <summary>Asks the server to heal this player.</summary>
        /// <param name="healAmount">Heal amount; non-positive values are ignored.</param>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestHealRpc(int healAmount)
        {
            // Healing does not revive: a downed player needs a teammate, and the dead stay dead.
            if (healAmount <= 0 || !IsStanding) return;

            // The role's healing perk is applied here rather than by the client asking: the client could claim any amount.
            var character = GetComponent<Characters.PlayerCharacter>();
            if (character != null) healAmount = Mathf.Max(1, Mathf.RoundToInt(healAmount * character.Perks.Healing));

            m_health.Value = Mathf.Clamp(m_health.Value + healAmount, 1, m_maxHealth);
        }

        // ---- Downed ----------------------------------------------------------------------------------

        /// <summary>Any client: asks the host to revive this downed player, as the local player.</summary>
        /// <remarks>Called on the downed player's copy by whoever finished the hold on it.</remarks>
        public void RequestRevive()
        {
            if (IsSpawned && IsDowned) RequestReviveRpc();
        }

        /// <remarks>
        /// Checked on the host rather than trusted: the reviver must be someone else, on their feet, and close by. The
        /// hold's length is the reviver's own to time; it is a co-op game, and nothing is gained by cheating it.
        /// </remarks>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestReviveRpc(RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (!IsDowned || sender == OwnerClientId) return;

            PlayerHealthSync reviver = null;
            foreach (var player in s_spawned)
            {
                if (player != null && player.OwnerClientId == sender) reviver = player;
            }

            if (reviver == null || !reviver.IsStanding) return;
            if (Vector3.Distance(reviver.transform.position, transform.position) > m_reviveReach) return;

            // The reviver's role decides how well they get someone back up: a Medic's patients stand up stronger.
            var character = reviver.GetComponent<Characters.PlayerCharacter>();
            var share = character != null ? character.Perks.ReviveHealth : 1f;
            m_health.Value = Mathf.Clamp(Mathf.RoundToInt(m_reviveHealth * share), 1, m_maxHealth);
            ServerRevived?.Invoke(this, reviver);
        }

        /// <summary>Server only: heals a player on their feet, e.g. from a teammate's healing ability.</summary>
        /// <returns>True if any health was restored.</returns>
        public bool ServerHeal(int amount)
        {
            if (!IsServer || amount <= 0 || !IsStanding || m_health.Value >= m_maxHealth) return false;

            m_health.Value = Mathf.Clamp(m_health.Value + amount, 1, m_maxHealth);
            return true;
        }

        /// <summary>Server only: gets a downed player back up without a reviver, e.g. when the night ends.</summary>
        public void ServerRevive()
        {
            if (!IsServer || !IsDowned) return;

            m_health.Value = Mathf.Clamp(m_reviveHealth, 1, m_maxHealth);
            ServerRevived?.Invoke(this, null);
        }

        private void GoDown()
        {
            // The clock first, in the same tick as the value, so no client counts down from a stale time.
            m_bleedOutAt.Value = NetworkManager.ServerTime.Time + m_bleedOutSeconds;
            m_health.Value = DownedValue;
            ServerDowned?.Invoke(this);
        }

        private void Kill()
        {
            if (IsDead) return;

            m_health.Value = DeadValue;
            ServerDied?.Invoke(this);
        }

        private bool AnyoneElseStanding()
        {
            foreach (var player in s_spawned)
            {
                if (player != null && player != this && player.IsStanding) return true;
            }

            return false;
        }

        // ---- Presentation ----------------------------------------------------------------------------

        private void HandleHealthChanged(int previous, int current)
        {
            // Only the owner runs the full UHFPS health stack; remote copies have PlayerHealth disabled, and
            // driving it would play this client's hurt sounds for someone else's hit.
            if (IsOwner) MirrorToLocalHealth(current, true);
            else if (current == DownedValue && previous > DownedValue) AnnounceDowned();

            HealthChanged?.Invoke(Mathf.Max(0, previous), Mathf.Max(0, current));
        }

        private void AnnounceDowned()
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager == null) return;

            var setup = GetComponent<HeroPlayerNetworkSetup>();
            var who = setup != null ? setup.DisplayName : "A teammate";
            gameManager.ShowHintMessage($"{who} is down! Hold Use on them to revive.", 4f);
        }

        /// <summary>The replicated value as UHFPS may see it: a downed player keeps 1, so its death never runs early.</summary>
        private void MirrorToLocalHealth(int value, bool playFeedback)
        {
            if (m_playerHealth == null) return;

            var local = value > DownedValue ? value : value == DownedValue ? 1 : 0;
            m_playerHealth.SetAuthoritativeHealth(local, playFeedback);
        }
    }
}
