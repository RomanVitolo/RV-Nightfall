using System;
using System.Collections.Generic;
using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Replicates player health, with the server as the sole authority over the value.
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
    /// </remarks>
    public class PlayerHealthSync : NetworkBehaviour, IPlayerHealthAuthority
    {
        [SerializeField] private PlayerHealth m_playerHealth;

        private readonly NetworkVariable<int> m_health =
            new(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private int m_maxHealth = 100;

        private static readonly List<PlayerHealthSync> s_spawned = new();

        /// <summary>Every player spawned in the level, on every client, whoever owns them.</summary>
        public static IReadOnlyList<PlayerHealthSync> Spawned => s_spawned;

        /// <summary>True once at least one player has spawned and every connected player is dead.</summary>
        /// <remarks>
        /// A player who left is despawned and disconnected, so they neither keep the others alive nor count as dead.
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

        /// <summary>Current replicated health. Readable on every client, written only by the server.</summary>
        public int Health => m_health.Value;

        /// <summary>Dead by the replicated health. Death is permanent until the level restarts.</summary>
        public bool IsDead => m_health.Value <= 0;

        /// <summary>Raised on every client when this player's health changes.</summary>
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

                // A resumed game starts each player at the health they were saved with. The client's account reached
                // the host before this spawn, which the save's spawn gate waits for. At least 1: a save made after
                // someone died must not resume them as a corpse with nothing to spectate.
                var world = FindAnyObjectByType<World.WorldSync>();
                if (world != null && world.TryGetSavedHealth(OwnerClientId, out var saved)) start = Mathf.Max(1, saved);

                m_health.Value = Mathf.Clamp(start, 0, m_maxHealth);
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
        /// </remarks>
        public void ApplyServerDamage(int damage)
        {
            if (!IsServer || damage <= 0 || m_health.Value <= 0) return;

            m_health.Value = Mathf.Clamp(m_health.Value - damage, 0, m_maxHealth);
        }

        /// <summary>Asks the server to heal this player.</summary>
        /// <param name="healAmount">Heal amount; non-positive values are ignored.</param>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestHealRpc(int healAmount)
        {
            // Healing does not revive: in multiplayer death is permanent until the level restarts.
            if (healAmount <= 0 || m_health.Value <= 0) return;

            // The role's healing perk is applied here rather than by the client asking: the client could claim any amount.
            var character = GetComponent<Characters.PlayerCharacter>();
            if (character != null) healAmount = Mathf.Max(1, Mathf.RoundToInt(healAmount * character.Perks.Healing));

            m_health.Value = Mathf.Clamp(m_health.Value + healAmount, 0, m_maxHealth);
        }

        private void HandleHealthChanged(int previous, int current)
        {
            // Only the owner runs the full UHFPS health stack; remote copies have PlayerHealth disabled, and
            // driving it would play this client's hurt sounds for someone else's hit.
            if (IsOwner) MirrorToLocalHealth(current, true);

            HealthChanged?.Invoke(previous, current);
        }

        private void MirrorToLocalHealth(int value, bool playFeedback)
        {
            if (m_playerHealth == null) return;

            m_playerHealth.SetAuthoritativeHealth(value, playFeedback);
        }
    }
}
